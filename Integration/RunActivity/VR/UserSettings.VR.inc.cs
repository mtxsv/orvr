// ORVR — VR settings block for Orts.Settings/UserSettings.cs.
//
// UserSettings auto-persists string/int/bool only, so VR floats use integer
// conventions (percent, millimeters). Insert inside the "#region User
// Settings" auto-property area (e.g. after ViewingFOV).

#region ORVR VR settings
[Default(false)]
public bool VREnabled { get; set; }
[Default(100)]
public int VRRenderScalePercent { get; set; }
[Default(63)]
public int VRIPDMillimeters { get; set; }
[Default(true)]
public bool VRHeadTracking { get; set; }
[Default(true)]
public bool VRPositionalTracking { get; set; }
[Default(350)]
public int VRHeadRadiusMM { get; set; }
[Default(300)]
public int VRHeadMaxUpMM { get; set; }
[Default(300)]
public int VRHeadMaxDownMM { get; set; }
[Default(true)]
public bool VRDominantHandRight { get; set; }
[Default(true)]
public bool VRHaptics { get; set; }
#endregion
