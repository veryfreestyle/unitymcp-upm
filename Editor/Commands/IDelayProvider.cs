using System;
using Cysharp.Threading.Tasks;

namespace VeryFS.UnityMCP.Editor.Commands
{
    // Time-delay seam for batch wait{ms}. Realtime so it does not depend on
    // Unity's scaled game time (which is 0 while the editor is paused).
    public interface IDelayProvider
    {
        UniTask Delay(int milliseconds);
    }

    public sealed class UniTaskDelayProvider : IDelayProvider
    {
        public UniTask Delay(int milliseconds)
            => UniTask.Delay(TimeSpan.FromMilliseconds(milliseconds), DelayType.Realtime);
    }
}
