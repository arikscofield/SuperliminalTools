local tas = require("tas").new()

tas.level("TestChamber_Live")

tas.wait(10)
tas.turn(180, 0, 1)
tas.wait(1)
tas.turn(180, 0, 1)

tas.wait(10)
tas.speed(10)
tas.hold_forward(1)
tas.wait(310)
tas.speed(1)
tas.wait(30)

tas.look_absolute(92, 0)
tas.wait(20)
tas.grab()
tas.wait(10)
tas.turn(0, 70, 20)
tas.ungrab()
tas.wait(10)
tas.turn(0, -70, 10)

tas.wait(30)
tas.jump(25)
tas.wait_until_checkpoint()
tas.reset_checkpoint()

tas.wait(5)
tas.look_absolute(112, 369)
tas.grab()
tas.turn(0, -15, 20)
tas.ungrab()

tas.wait(55)
tas.jump(25)
tas.wait_until_grounded()
tas.wait(10)
tas.jump(25)

tas.wait(50)