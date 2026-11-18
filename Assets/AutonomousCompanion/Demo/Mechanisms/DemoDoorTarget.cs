namespace AutonomousCompanion.Demo
{
    /// <summary>Observable mechanism state. Leave arrivalNode null to prevent attempting
    /// to stand inside a door; the navigation graph separately crosses its gate.</summary>
    public sealed class DemoDoorTarget : CompanionTarget
    {
        public DemoDoor door;
        public override bool Active { get { return door != null && door.IsOpen; } }
        private void Reset() { kind = "door"; }
    }
}
