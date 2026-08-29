using System;
using System.Collections.Generic;

namespace GomLib.GomTypes {
  public class Timer : GomType {
    public Timer() : base(GomTypeId.Timer) { }

    // Serialized HeroTimer layout, matching Jedipedia's node reader. Numeric members use
    // SWTOR varints (timerState is constrained to the non-negative states 1..3) except the
    // one-byte realTime flag. Older streams omit the
    // final tick fields unless the 0xDEADBEEF marker is present.
    public override Object ReadData(DataObjectModel dom, GomBinaryReader reader) {
      long stateSince = reader.ReadSignedNumber();
      long timerState = reader.ReadSignedNumber();
      if (timerState < 1 || timerState > 3) throw new InvalidOperationException($"Invalid Timer state {timerState}.");
      byte realTimeRaw = reader.ReadByte();
      if (realTimeRaw > 1) throw new InvalidOperationException($"Invalid Timer realTime value {realTimeRaw}.");

      long startTime = reader.ReadSignedNumber();
      long stopTime = reader.ReadSignedNumber();
      long lastFired = reader.ReadSignedNumber();
      long fireRate = reader.ReadSignedNumber();
      long elapsedTime = reader.ReadSignedNumber();
      long marker = reader.ReadSignedNumber();
      long script = reader.ReadSignedNumber();

      long? tickCount = null;
      long? elapsedTicks = null;
      if (unchecked((ulong)marker) == 0xDEADBEEFUL) {
        tickCount = reader.ReadSignedNumber();
        elapsedTicks = reader.ReadSignedNumber();
      }

      return new Dictionary<string, object> {
        ["stateSince"] = stateSince,
        ["timerState"] = timerState,
        ["realTime"] = realTimeRaw != 0,
        ["startTime"] = startTime,
        ["stopTime"] = stopTime,
        ["lastFired"] = lastFired,
        ["fireRate"] = fireRate,
        ["elapsedTime"] = elapsedTime,
        ["script"] = script,
        ["tickCount"] = tickCount,
        ["elapsedTicks"] = elapsedTicks
      };
    }
    public override System.String ToString() => "Timer";
  }
}
