using System;
using System.Runtime.CompilerServices;
using BDArmory.Competition;

// M2 hook calls (same rules as HookCalls.cs): this class touches Overdrive-only members, is JIT-compiled only when the probe
// found the hook (F10 here), keeps the handlers in Delegate-typed fields and builds every delegate explicitly, so nothing
// in the assembly's metadata names an Overdrive-only type (StockBdaLoadTests).
namespace Skizzy.Bda.Ksp
{
    /// <summary>
    /// F10 competition scoring events for the M2 missile ledger: <c>CompetitionScores.OnHit</c> (which vessel a missile
    /// explosion actually damaged) and <c>CompetitionScores.OnDeath</c> with its attacker and damage type. BDArmory raises
    /// them only while a competition runs.
    /// </summary>
    internal static class HookScores
    {
        private static Delegate _hHit;
        private static Delegate _hDeath;
        private static Action<string, string, int, int> _hit, _death;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Subscribe(Action<string, string, int, int> hit, Action<string, string, int, int> death)
        {
            _hit = hit;
            _death = death;
            CompetitionScores.OnHit -= (Action<string, string, DamageFrom, int>)_hHit;
            { var d = new Action<string, string, DamageFrom, int>(OnHit); CompetitionScores.OnHit += d; _hHit = d; }
            CompetitionScores.OnDeath -= (Action<string, string, DamageFrom, AliveState>)_hDeath;
            { var d = new Action<string, string, DamageFrom, AliveState>(OnDeath); CompetitionScores.OnDeath += d; _hDeath = d; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Unsubscribe()
        {
            CompetitionScores.OnHit -= (Action<string, string, DamageFrom, int>)_hHit;
            _hHit = null;
            CompetitionScores.OnDeath -= (Action<string, string, DamageFrom, AliveState>)_hDeath;
            _hDeath = null;
            _hit = _death = null;
        }

        private static void OnHit(string attacker, string victim, DamageFrom from, int parts)
        {
            try { _hit?.Invoke(attacker, victim, (int)from, parts); }
            catch (Exception) { }
        }

        private static void OnDeath(string victim, string attacker, DamageFrom from, AliveState state)
        {
            try { _death?.Invoke(victim, attacker, (int)from, (int)state); }
            catch (Exception) { }
        }
    }
}
