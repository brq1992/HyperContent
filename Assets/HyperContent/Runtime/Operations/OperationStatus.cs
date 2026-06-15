namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Operation lifecycle states. See ARCHITECTURE.md section 5.1 for the full state machine.
    /// </summary>
    public enum OperationStatus
    {
        None = 0,
        Pending,
        InProgress,
        Succeeded,
        Failed,
        Disposed
    }
}
