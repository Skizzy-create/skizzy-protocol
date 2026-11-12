namespace Skizzy.C2
{
    /// <summary>
    /// Optional control a binding offers next to <see cref="IBdaControl"/>: switching a unit's radars (BDArmory radar
    /// modules) on or off, for the <c>emcon</c> doctrine key and bda.set_radar. Returns false when the unit has no radar.
    /// </summary>
    public interface IRadarControl
    {
        bool SetRadarActive(uint unit, bool active);
    }
}
