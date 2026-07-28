// SuperliminalDeterminism - makes Superliminal's physics bit-identical across CPU vendors.
//
// RCPPS / RCPSS / RSQRTPS / RSQRTSS are only defined to about 12 bits of
// precision, and their exact results are microarchitecture-specific.
// PhysX uses them inside the rigidbody contact solver,
// so a demo recorded on one vendor can desync when replayed on the other.
//
// Every listed site is overwritten with UD2. A vectored exception handler
// catches the resulting #UD, computes the value with sqrtf and division - both
// exactly specified by IEEE 754, and therefore identical on every x86 CPU -
// writes it to the destination register, and resumes after the original
// instruction.
//
// Site lists are generated per game version from UnityPlayer.dll, filtered to
// real code (inside a .pdata RUNTIME_FUNCTION), register-register operands, and
// non-VEX encodings. VEX 256-bit sites cannot be emulated this way, because a
// CONTEXT does not carry the upper halves of the ymm registers; they were
// verified never to execute on the physics path.
//
// Build: cl /LD /O2 /EHsc determinism.cpp /link /OUT:SuperliminalDeterminism.dll

#include <windows.h>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <unordered_map>

struct Site {
    uint8_t  len;      // full instruction length in bytes
    uint8_t  op;       // 0x52 = rsqrt, 0x53 = rcp
    uint8_t  dst;      // xmm register index 0-15
    uint8_t  src;
    bool     scalar;   // F3 prefix -> ss form, 1 lane instead of 4
    uint64_t hits;
};

static std::unordered_map<uint64_t, Site> g_sites;
static PVOID g_veh = nullptr;

// Parse [F3] [REX] 0F 52|53 modrm, register-register form only.
static bool DecodeAt(const uint8_t* p, Site& s) {
    int i = 0; uint8_t rex = 0;
    s.scalar = false;
    if (p[i] == 0xF3) { s.scalar = true; i++; }
    if ((p[i] & 0xF0) == 0x40) { rex = p[i]; i++; }
    if (p[i] != 0x0F) return false;
    i++;
    if (p[i] != 0x52 && p[i] != 0x53) return false;
    s.op = p[i]; i++;
    uint8_t modrm = p[i];
    if ((modrm & 0xC0) != 0xC0) return false;                       // memory operand
    s.dst = (uint8_t)(((modrm >> 3) & 7) | ((rex & 0x04) ? 8 : 0)); // REX.R
    s.src = (uint8_t)(( modrm       & 7) | ((rex & 0x01) ? 8 : 0)); // REX.B
    i++;
    s.len = (uint8_t)i;
    s.hits = 0;
    return true;
}

static LONG CALLBACK Handler(EXCEPTION_POINTERS* ep) {
    if (ep->ExceptionRecord->ExceptionCode != EXCEPTION_ILLEGAL_INSTRUCTION)
        return EXCEPTION_CONTINUE_SEARCH;

    auto it = g_sites.find(ep->ContextRecord->Rip);
    if (it == g_sites.end()) return EXCEPTION_CONTINUE_SEARCH;
    Site& s = it->second;

    // Xmm0..Xmm15 are contiguous in the x64 CONTEXT union.
    M128A* xmm = &ep->ContextRecord->Xmm0;
    const float* src = reinterpret_cast<const float*>(&xmm[s.src]);
    float*       dst = reinterpret_cast<float*>(&xmm[s.dst]);

    // Exact IEEE sqrt and divide: bit-identical on every x86 CPU.
    const int lanes = s.scalar ? 1 : 4;
    float out[4];
    for (int i = 0; i < lanes; i++)
        out[i] = (s.op == 0x52) ? (1.0f / sqrtf(src[i])) : (1.0f / src[i]);
    for (int i = 0; i < lanes; i++) dst[i] = out[i];   // ss form leaves lanes 1-3

    s.hits++;
    ep->ContextRecord->Rip += s.len;
    return EXCEPTION_CONTINUE_EXECUTION;
}

// Returns the number of sites patched, or a negative error code:
//   -1 UnityPlayer.dll not loaded   -2 bad arguments   -3 handler registration failed
extern "C" __declspec(dllexport)
int InstallDeterminismPatches(const uint32_t* rvas, int count) {
    HMODULE up = GetModuleHandleA("UnityPlayer.dll");
    if (!up) return -1;
    if (!rvas || count <= 0) return -2;

    // Register before patching, so no site can trap before we can service it.
    if (!g_veh) g_veh = AddVectoredExceptionHandler(1, Handler);
    if (!g_veh) return -3;

    int patched = 0;
    for (int i = 0; i < count; i++) {
        uint8_t* p = reinterpret_cast<uint8_t*>(up) + rvas[i];

        Site s;
        if (!DecodeAt(p, s)) continue;      // not what we expected: leave alone

        DWORD old;
        if (!VirtualProtect(p, s.len, PAGE_EXECUTE_READWRITE, &old)) continue;
        g_sites[reinterpret_cast<uint64_t>(p)] = s;
        p[0] = 0x0F; p[1] = 0x0B;           // UD2; remaining bytes never execute
        VirtualProtect(p, s.len, old, &old);
        FlushInstructionCache(GetCurrentProcess(), p, s.len);
        patched++;
    }
    return patched;
}

// Diagnostic: per-site trap counts, for confirming the patch is live and hot.
extern "C" __declspec(dllexport)
void DumpDeterminismHits(const char* outFile) {
    FILE* f = nullptr;
    if (fopen_s(&f, outFile, "w") != 0 || !f) return;
    uint64_t base = reinterpret_cast<uint64_t>(GetModuleHandleA("UnityPlayer.dll"));
    uint64_t total = 0;
    for (auto& kv : g_sites) {
        if (kv.second.hits)
            fprintf(f, "%llx %llu\n",
                    (unsigned long long)(kv.first - base),
                    (unsigned long long)kv.second.hits);
        total += kv.second.hits;
    }
    fprintf(f, "TOTAL %llu across %zu sites\n", (unsigned long long)total, g_sites.size());
    fclose(f);
}
