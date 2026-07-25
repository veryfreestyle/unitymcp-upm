using Cysharp.Threading.Tasks;
using UnityEditor;
using VeryFS.UnityMCP.Editor.Infrastructure;

namespace VeryFS.UnityMCP.Editor.Commands.GameView
{
    public interface IEditorUpdateAwaiter
    {
        UniTask NextUpdate();
    }

    public sealed class UnityEditorUpdateAwaiter : IEditorUpdateAwaiter
    {
        public UniTask NextUpdate()
        {
            var completion = new UniTaskCompletionSource();
            void Complete()
            {
                EditorApplication.update -= Complete;
                completion.TrySetResult();
            }
            EditorApplication.update += Complete;
            EditorApplication.QueuePlayerLoopUpdate();
            return completion.Task;
        }
    }

    public interface IGameViewSettleWaiter
    {
        UniTask<bool> WaitAsync(IGameViewTarget target);
    }

    public sealed class GameViewSettleWaiter : IGameViewSettleWaiter
    {
        public const int RenderTextureSettleTimeoutMs = 3000;

        private readonly IEditorUpdateAwaiter updates;
        private readonly IClock clock;

        public GameViewSettleWaiter(IEditorUpdateAwaiter updates, IClock clock)
        {
            this.updates = updates;
            this.clock = clock;
        }

        public async UniTask<bool> WaitAsync(IGameViewTarget target)
        {
            var deadline = clock.UtcNow.AddMilliseconds(RenderTextureSettleTimeoutMs);

            // Do not sample the pre-Repaint RT. Give the Editor at least one update
            // to process the selected resolution or dock-layout change.
            await updates.NextUpdate();

            bool hasPrevious = false;
            int previousWidth = 0;
            int previousHeight = 0;
            while (clock.UtcNow < deadline)
            {
                await updates.NextUpdate();

                if (!target.TryGetRenderTextureSize(out int width, out int height))
                {
                    hasPrevious = false;
                    continue;
                }

                if (hasPrevious && width == previousWidth && height == previousHeight)
                {
                    return true;
                }

                hasPrevious = true;
                previousWidth = width;
                previousHeight = height;
            }

            return false;
        }
    }
}
