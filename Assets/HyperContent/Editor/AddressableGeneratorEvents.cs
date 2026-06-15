using System;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Raised when RuleToAddressableGenerator finishes Create All or ReImport All.
    /// Subscribe from HyperContent.Editor; RuleToAddressableGenerator invokes after work completes.
    /// </summary>
    public static class AddressableGeneratorEvents
    {
        public static event Action OnCreateAllCompleted;
        public static event Action OnReimportAllCompleted;
        /// <summary>RuleToAddressableGenerator single-row Create button finished (one group).</summary>
        public static event Action OnSingleGroupBuildCompleted;

        public static void InvokeCreateAllCompleted()
        {
            OnCreateAllCompleted?.Invoke();
        }

        public static void InvokeReimportAllCompleted()
        {
            OnReimportAllCompleted?.Invoke();
        }

        public static void InvokeSingleGroupBuildCompleted()
        {
            OnSingleGroupBuildCompleted?.Invoke();
        }
    }
}
