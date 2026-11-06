namespace Haiven.Sdk
{
    /// <summary>
    /// Where a host-facing socket raises its events. A finite choice rather than
    /// a flag, because the engine-quirks plan names these as adapter ids
    /// (<c>dispatch.sync-context</c>, <c>dispatch.direct</c>) and more may follow
    /// (<c>dispatch.game-thread</c> for Unreal).
    /// </summary>
    public enum HaivenDispatch
    {
        /// <summary>
        /// Posted to the host's main <see cref="System.Threading.SynchronizationContext"/>,
        /// captured where the socket is created (<c>dispatch.sync-context</c>).
        /// </summary>
        MainThread,

        /// <summary>
        /// Raised on whichever thread the transport uses, for a host that
        /// marshals for itself (<c>dispatch.direct</c>).
        /// </summary>
        Direct,
    }
}
