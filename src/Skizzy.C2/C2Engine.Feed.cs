using System.Collections.Generic;
using Skizzy.C2.Tactical;

namespace Skizzy.C2
{
    public sealed partial class C2Engine
    {
        // M2: when the world is the track picture engine (TpeWorld), each side's picture is copied from the TPE's
        // per-team table instead of re-reading every unit's sensors here (BuildPictures' legacy path).
        /// <summary>
        /// Optional listener that sees every hook event as it arrives (main thread, before the engine queues it for its
        /// next tick): the host publishes them as bda.hook.* events so the hooks can be tested without the engine
        /// ticking. Must not throw.
        /// </summary>
        public IHookEvents Tap;

        private IC2PictureFeed _feed;

        /// <summary>True when the engine builds its pictures from a track picture feed (<see cref="IC2PictureFeed"/>).</summary>
        public bool UsesPictureFeed => _feed != null;

        private IRadarControl _radar;

        /// <summary>True when the binding can switch units' radars (doctrine emcon).</summary>
        public bool HasRadarControl => _radar != null;

        private void AttachFeed()
        {
            _radar = _ctl as IRadarControl;
            _feed = _world as IC2PictureFeed;
            if (_feed != null) _feed.IdentifyRange = FeedIdentifyRange;
        }

        /// <summary>The doctrine identification range of a holder (row 24), for the TPE's identification test.</summary>
        private double FeedIdentifyRange(uint holder)
        {
            var r = Rec(holder);
            if (r == null) return double.NaN;
            if (r.S.IsMissile) return 0;
            ResolveDoctrine(r, false);
            return r.Doc.ClassificationRangeM(r.S.Class);
        }

        private void PicturesFromFeed()
        {
            // own-sensor contacts (StepDivert): copied only when the TPE re-read them
            for (int i = 0; i < _unitList.Count; i++)
            {
                var r = _unitList[i];
                if (!r.Alive || r.S.IsMissile || r.P == null || !r.P.Active)
                {
                    if (r.Contacts.Count > 0) r.Contacts.Clear();
                    r.ContactsDue = int.MinValue;
                    continue;
                }
                r.ContactsDue = _feed.OwnContacts(r.Id, r.Contacts, r.ContactsDue);
            }
            for (int s = 0; s < _sideList.Count; s++)
            {
                var p = _sideList[s];
                if (!p.Active) continue;
                var list = _feed.SidePicture(p.Side);
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    var t = Rec(e.Target);
                    if (t == null || !t.Alive || t.P == null || !HostileSides(p, t.P)) continue;
                    if (e.Holders > 0)
                    {
                        p.Holders[t.Id] = e.Holders;
                        if (e.Holders == 1) p.SoleHolder[t.Id] = e.SoleHolder;
                        if (e.SurveillanceHeld) p.SurveillanceHeld.Add(t.Id);
                    }
                    p.InView.Add(t.Id);
                    p.LastInView[t.Id] = _now;
                    if (t.S.IsMissile) { p.HostileMissiles.Add(t.Id); p.MissileRecs.Add(t); }
                    else { p.Hostiles.Add(t.Id); p.HostileRecs.Add(t); }
                    if (e.IdentifiedBy != 0 && !t.S.IsMissile && !p.Identified.Contains(t.Id))
                    {
                        var by = Rec(e.IdentifiedBy);
                        if (by != null && by.P == p)
                        {
                            p.Identified.Add(t.Id);
                            OnIdentified(p, by, t);
                        }
                    }
                }
                ReportOwnSensorOnly(p);
            }
        }
    }
}
