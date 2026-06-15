using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Unity 2022-compatible async helpers. Public HyperContent await entry points use <c>System.Threading.Tasks.Task</c>
    /// (not Unity 6 <c>Awaitable</c>) for broad editor/runtime compatibility.
    /// </summary>
    public static class UnityAsyncCompat
    {
        public static async Task WaitForWebRequestAsync(UnityWebRequest request)
        {
            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();
        }

        public static async Task NextFrameAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
