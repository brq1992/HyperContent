namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Provider Layer interface: pluggable IO providers registered by ProviderId.
    /// See ARCHITECTURE.md section 6.1.
    /// </summary>
    public interface IContentProvider
    {
        string ProviderId { get; }
        void Provide(ProvideHandle handle);
        void Release(ProvideHandle handle);
    }
}
