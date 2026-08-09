namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class McpStageInputProbe : IMcpStageInputProbe
    {
        public bool TryBind(out McpStageInputBinding binding, out string reason)
        {
            bool ok = McpStageInputBinding.TryBind(out binding, out string missing);
            reason = ok ? null : "missing member: " + missing;
            return ok;
        }
    }
}
