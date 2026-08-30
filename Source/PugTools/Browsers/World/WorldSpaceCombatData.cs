using System;
using System.Collections.Generic;
using FileFormats;
using SlimDX;

namespace PugTools {
  internal sealed class WorldSpaceCombatEncounterSpec {
    public String TriggerFqn { get; set; }
    public String EncounterName { get; set; }
    public String PathName { get; set; }
    public String VehicleSpec { get; set; }
    public String ModelPath { get; set; }
    public GR2 Model { get; set; }
    public String EngineFx { get; set; }
    public String AnchorOffset { get; set; }
    public Vector3 AnchorOffsetVector { get; set; }
    public Boolean HasAnchorOffset { get; set; }
    public String FormationKind { get; set; }
    // Offsets are authored in the leader's local path frame. An empty list means one ship at the path origin.
    public readonly List<Vector3> FormationOffsets = new List<Vector3>();
    public Single ModelScale { get; set; } = 1f;
    public Single SpawnDelay { get; set; }
    public Single SpeedScale { get; set; } = 1f;
    public Int32 ShipCount { get; set; } = 1;
    public Int32 BoltedCount { get; set; }
    public Boolean Boss { get; set; }
  }
}
