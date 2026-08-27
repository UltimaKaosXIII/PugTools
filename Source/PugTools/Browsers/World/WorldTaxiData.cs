using System.Collections.Generic;
using FileFormats;

namespace PugTools {
  /// <summary>
  /// A taxi route that can be previewed directly in the World Browser. The route is resolved against the
  /// currently loaded area's authored path list, so the renderer never has to touch the GOM while riding.
  /// </summary>
  internal sealed class WorldTaxiRouteInfo {
    public string SourceFqn { get; set; }
    public string SourceLabel { get; set; }
    public string DestinationFqn { get; set; }
    public string DestinationLabel { get; set; }
    public string PathFqn { get; set; }
    public string Label { get; set; }
    public AreaPath Path { get; set; }
    public bool Reversed { get; set; }
    public int Cost { get; set; } = -1;
    public bool FromTaxiGom { get; set; }

    // A route shown to the user may be a shortest-path journey across several authored tax.* links. Direct routes
    // leave Legs empty, preserving the old data shape. Composite routes keep the exact authored legs so the map can
    // draw every spline and the ride controller can continue seamlessly at intermediate terminals.
    public List<WorldTaxiRouteInfo> Legs { get; set; }
    public int HopCount { get { return Legs != null && Legs.Count > 0 ? Legs.Count : 1; } }

    // taxVehicleSpec resolves through spnVehicleDataPrototype -> vehAppearanceProtoData. Keeping the already
    // decoded GR2 on the route means the render thread never has to touch the GOM or TOR archives during a ride.
    public string VehicleSpec { get; set; }
    public string VehicleAppearance { get; set; }
    public string VehicleModelPath { get; set; }
    public GR2 VehicleModel { get; set; }
    public float VehicleScale { get; set; } = 1f;
    public bool VehicleFallback { get; set; }
  }
}
