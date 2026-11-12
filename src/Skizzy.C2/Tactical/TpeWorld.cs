using System;
using System.Collections.Generic;

namespace Skizzy.C2.Tactical
{
    /// <summary>
    /// The C2 engine's world when it runs over the track picture engine (M2): an <see cref="IC2World"/> that forwards
    /// unit states and hostility to the game binding, serves side detection and friendlies-engaging from the TPE's
    /// tables, and hands the engine each side's built picture (<see cref="IC2PictureFeed"/>), so the engine no longer
    /// reads every unit's sensors itself. Control calls still go straight to the binding's <see cref="IBdaControl"/>.
    /// </summary>
    public sealed class TpeWorld : IC2World, IC2PictureFeed
    {
        public readonly IC2World Inner;
        public readonly TrackPictureEngine Tpe;

        /// <summary>
        /// True: every engine read of the units steps the TPE first and serves the TPE's copies (tests, the harness).
        /// False: the host steps the TPE on its own schedule and units come fresh from the binding.
        /// </summary>
        public bool StepOnRead = true;

        public TpeWorld(IC2World inner, ITacticalSensors sensors = null)
        {
            Inner = inner ?? throw new ArgumentNullException(nameof(inner));
            Tpe = new TrackPictureEngine(inner, sensors);
        }

        public TpeWorld(TrackPictureEngine tpe)
        {
            Tpe = tpe ?? throw new ArgumentNullException(nameof(tpe));
            Inner = tpe.World;
        }

        public double Now => Inner.Now;
        public double BodyRadius => Inner.BodyRadius;
        public HookSet Hooks => Inner.Hooks;

        public void GetUnits(List<UnitState> into)
        {
            if (StepOnRead)
            {
                Tpe.Step();
                Tpe.GetUnits(into);
            }
            else Inner.GetUnits(into);
        }

        public void GetOwnContacts(uint unit, List<uint> into) { Inner.GetOwnContacts(unit, into); }

        public bool SideDetects(string side, uint target) { return Tpe.InView(side, target) || Inner.SideDetects(side, target); }

        public bool AreHostile(string sideA, string sideB) { return Inner.AreHostile(sideA, sideB); }

        public int FriendliesEngaging(string side, uint target, uint exceptUnit)
        {
            return Tpe.HasEngagementData && Tpe.Steps > 0 ? Tpe.FriendliesEngaging(side, target, exceptUnit) : Inner.FriendliesEngaging(side, target, exceptUnit);
        }

        public List<PictureEntry> SidePicture(string side) { return Tpe.Steps > 0 ? Tpe.SidePicture(side) : null; }

        public int OwnContacts(uint unit, List<uint> into, int knownStamp) { return Tpe.OwnContacts(unit, into, knownStamp); }

        public Func<uint, double> IdentifyRange
        {
            get { return Tpe.IdentifyRange; }
            set { Tpe.IdentifyRange = value; }
        }
    }
}
