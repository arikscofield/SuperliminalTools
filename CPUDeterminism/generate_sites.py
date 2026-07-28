#!/usr/bin/env python3
"""Generate the approximate-reciprocal site list for a Superliminal build.

Scans UnityPlayer.dll for RCPPS / RCPSS / RSQRTPS / RSQRTSS instructions and
writes their RVAs to Sites/rsqrt_sites_<version>.txt, which the plugin
embeds at build time.

Why: those instructions are only defined to ~12 bits of precision and their
exact results are microarchitecture-specific, so Intel and AMD disagree. PhysX
uses them in the rigidbody contact solver, which makes demos desync across CPU
vendors. SuperliminalDeterminism.dll rewrites each listed site to trap, and
emulates it with exact IEEE arithmetic instead.

Three classes of hit are deliberately excluded:

  data  Bytes that decode as an instruction but lie outside every .pdata
        RUNTIME_FUNCTION, i.e. they are not code. Patching them would corrupt
        data. (Typically ~4 per build.)
  mem   Memory-operand forms. The handler reads its source from the CONTEXT's
        xmm registers and has no effective-address decoder. (~2-3 per build.)
  vex   VEX-encoded 256-bit forms. A CONTEXT does not carry the upper halves of
        the ymm registers, so they cannot be emulated this way. (~12 per build.)
        These were verified never to execute on the physics path; if that ever
        changes they would need code-cave trampolines instead.

Requires: pip install capstone pefile

Usage:
  python generate_sites.py "C:/Games/Superliminal/1.10.2023.2.17/UnityPlayer.dll"
  python generate_sites.py <dll> --version 1.10.2023.2.17 --out ./Sites

"""

import argparse
import bisect
import os
import struct
import sys

try:
    import capstone
    import pefile
except ImportError:
    sys.exit("Missing dependencies. Run: pip install capstone pefile")

TARGET_MNEMONICS = {
    "rcpps", "rcpss", "rsqrtps", "rsqrtss",
    "vrcpps", "vrcpss", "vrsqrtps", "vrsqrtss",
}


def function_ranges(pe):
    """[BeginAddress, EndAddress) for every function, from the .pdata directory.

    Used to reject byte sequences that merely decode as an instruction but sit
    in data. Every real x64 function has a RUNTIME_FUNCTION entry.
    """
    directory = pe.OPTIONAL_HEADER.DATA_DIRECTORY[3]  # IMAGE_DIRECTORY_ENTRY_EXCEPTION
    if not directory.VirtualAddress or not directory.Size:
        return None
    raw = pe.get_data(directory.VirtualAddress, directory.Size)
    ranges = []
    for offset in range(0, len(raw) - 11, 12):
        begin, end, _unwind = struct.unpack_from("<III", raw, offset)
        if begin or end:
            ranges.append((begin, end))
    ranges.sort()
    return ranges


def scan(dll_path):
    pe = pefile.PE(dll_path)
    if pe.FILE_HEADER.Machine != 0x8664:
        sys.exit(f"{dll_path} is not an x64 image.")

    image_base = pe.OPTIONAL_HEADER.ImageBase
    ranges = function_ranges(pe)
    if not ranges:
        sys.exit(f"{dll_path} has no .pdata; cannot distinguish code from data.")
    starts = [r[0] for r in ranges]

    def is_code(rva):
        i = bisect.bisect_right(starts, rva) - 1
        return i >= 0 and ranges[i][0] <= rva < ranges[i][1]

    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    md.skipdata = True  # keep sweeping past bytes that do not decode

    kept = []
    skipped = {"data": 0, "mem": 0, "vex": 0}

    for section in pe.sections:
        if not section.Characteristics & 0x20000000:  # IMAGE_SCN_MEM_EXECUTE
            continue
        for insn in md.disasm(section.get_data(), image_base + section.VirtualAddress):
            if insn.id == 0 or insn.mnemonic not in TARGET_MNEMONICS:
                continue
            rva = insn.address - image_base
            if not is_code(rva):
                skipped["data"] += 1
            elif insn.bytes[0] in (0xC4, 0xC5):
                skipped["vex"] += 1
            elif any(o.type == capstone.x86.X86_OP_MEM for o in insn.operands):
                skipped["mem"] += 1
            else:
                kept.append(rva)

    pe.close()
    return kept, skipped


def main():
    parser = argparse.ArgumentParser(
        description="Generate a determinism site list from UnityPlayer.dll.")
    parser.add_argument("dll", help="path to the game's UnityPlayer.dll")
    parser.add_argument("--version", help="game version label; "
                                          "defaults to the DLL's parent folder name")
    parser.add_argument("--out", default=None,
                        help="output directory (default: Sites/ next to this script)")
    args = parser.parse_args()

    if not os.path.isfile(args.dll):
        sys.exit(f"Not found: {args.dll}")

    version = args.version or os.path.basename(os.path.dirname(os.path.abspath(args.dll)))
    out_dir = args.out or os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                       "Sites")
    out_dir = os.path.abspath(out_dir)
    os.makedirs(out_dir, exist_ok=True)

    print(f"scanning {args.dll} ({os.path.getsize(args.dll):,} bytes) ...")
    kept, skipped = scan(args.dll)

    if not kept:
        sys.exit("No sites found. Is this really a Unity player binary?")

    out_path = os.path.join(out_dir, f"rsqrt_sites_{version}.txt")
    with open(out_path, "w") as f:
        f.write(f"# Approximate-reciprocal sites in UnityPlayer.dll for {version}\n")
        f.write(f"# Generated by generate_sites.py; RVAs in hex, one per line.\n")
        for rva in kept:
            f.write(f"{rva:X}\n")

    print(f"wrote {len(kept)} sites -> {out_path}")
    print(f"excluded: {skipped['data']} data, {skipped['mem']} memory-operand, "
          f"{skipped['vex']} VEX-256")
    print()
    print(f"Next: add a GameVersion branch for {version} to the csproj if this is a")
    print("new build, then rebuild so the list is embedded in the plugin.")


if __name__ == "__main__":
    main()
