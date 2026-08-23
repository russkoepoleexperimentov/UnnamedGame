using UnnamedGame;

Console.WriteLine("""
    UnnamedGame — MVP
      WASD      move (throttle, brake and steering when driving)
      Space     jump (hold to bunny-hop) / handbrake in the car
      Ctrl      crouch; in mid-air it pulls your legs up (crouch-jump onto high ledges)
      Mouse     look
      LMB / F   fire a physics ball
      E         enter / leave the car
      L         flashlight on foot, headlights in the car
      R / F1-F2 / N / B   default binds from config.cfg (respawn, overlay, noclip, spawn)
      ~         console (help lists commands, player_debug 1 for the overlay)
      Esc       release the mouse (click the window to capture it)
    """);

using var game = new Game();
game.Run();
