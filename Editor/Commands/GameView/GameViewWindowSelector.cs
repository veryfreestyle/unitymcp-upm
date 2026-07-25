using System.Collections.Generic;
using UnityEditor;

namespace VeryFS.UnityMCP.Editor.Commands.GameView
{
    public sealed class GameViewWindowSelection
    {
        private GameViewWindowSelection(
            bool ok, EditorWindow window, string errorCode, string error)
        {
            Ok = ok;
            Window = window;
            ErrorCode = errorCode;
            Error = error;
        }

        public bool Ok { get; }
        public EditorWindow Window { get; }
        public string ErrorCode { get; }
        public string Error { get; }

        public static GameViewWindowSelection Success(EditorWindow window)
            => new GameViewWindowSelection(true, window, null, null);

        public static GameViewWindowSelection Failure(string errorCode, string error)
            => new GameViewWindowSelection(false, null, errorCode, error);
    }

    public static class GameViewWindowSelector
    {
        public static GameViewWindowSelection Select(
            IReadOnlyList<EditorWindow> windows, EditorWindow focusedWindow)
        {
            if (windows == null || windows.Count == 0)
            {
                return GameViewWindowSelection.Failure(
                    "game_view_unavailable", "No Game View window is open.");
            }

            if (focusedWindow != null)
            {
                for (int i = 0; i < windows.Count; i++)
                {
                    if (ReferenceEquals(windows[i], focusedWindow))
                    {
                        return GameViewWindowSelection.Success(focusedWindow);
                    }
                }
            }

            if (windows.Count == 1)
            {
                return GameViewWindowSelection.Success(windows[0]);
            }

            return GameViewWindowSelection.Failure(
                "ambiguous_game_view",
                "Multiple Game View windows are open and none is focused.");
        }
    }
}
