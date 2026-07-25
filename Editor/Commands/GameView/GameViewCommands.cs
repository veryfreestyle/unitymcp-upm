using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.GameView
{
    public sealed class GameViewGetStateCommand : IGroupedCommand, IAsyncRpcCommand
    {
        private readonly IGameViewEnvironment environment;
        public GameViewGetStateCommand(IGameViewEnvironment environment)
            => this.environment = environment;

        public string Method => RpcMethods.GameViewGetState;
        public string Group => RpcMethods.GameViewGroup;
        public string Action => "get-state";
        public RpcToolDescriptor Descriptor => GameViewCommandSupport.Descriptor(
            "game-view-get-state", Method, "Game View / Get State",
            "Get the selected Game View resolution, maximize state, and actual render texture size.",
            JsonRpcSerializer.Object(), true);

        public JsonRpcResponse Handle(JsonRpcRequest request)
            => GameViewCommandSupport.AsyncOnly(request.Id);

        public UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request)
        {
            var lookup = environment.FindTarget();
            if (!lookup.Ok)
            {
                if (lookup.ErrorCode == "game_view_unavailable")
                {
                    return UniTask.FromResult(JsonRpcResponse.FromSuccess(
                        request.Id, JsonRpcSerializer.Object(("available", false))));
                }
                return UniTask.FromResult(GameViewCommandSupport.TargetError(request.Id, lookup));
            }

            return UniTask.FromResult(JsonRpcResponse.FromSuccess(
                request.Id,
                GameViewCommandSupport.BuildState(
                    lookup.Target, environment.ListResolutions())));
        }
    }

    public sealed class GameViewListResolutionsCommand : IGroupedCommand, IAsyncRpcCommand
    {
        private readonly IGameViewEnvironment environment;
        public GameViewListResolutionsCommand(IGameViewEnvironment environment)
            => this.environment = environment;

        public string Method => RpcMethods.GameViewListResolutions;
        public string Group => RpcMethods.GameViewGroup;
        public string Action => "list-resolutions";
        public RpcToolDescriptor Descriptor => GameViewCommandSupport.Descriptor(
            "game-view-list-resolutions", Method, "Game View / List Resolutions",
            "List every resolution entry currently available in the Game View dropdown.",
            JsonRpcSerializer.Object(), true);

        public JsonRpcResponse Handle(JsonRpcRequest request)
            => GameViewCommandSupport.AsyncOnly(request.Id);

        public UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request)
        {
            IReadOnlyList<GameViewResolutionInfo> resolutions = environment.ListResolutions();
            var array = new JsonData();
            array.SetJsonType(JsonType.Array);
            for (int i = 0; i < resolutions.Count; i++)
            {
                array.Add(GameViewCommandSupport.ResolutionJson(resolutions[i]));
            }

            var result = JsonRpcSerializer.Object(("resolutions", array));
            var lookup = environment.FindTarget();
            if (lookup.Ok)
            {
                result["selectedIndex"] = lookup.Target.SelectedResolutionIndex;
            }

            return UniTask.FromResult(JsonRpcResponse.FromSuccess(request.Id, result));
        }
    }

    public sealed class GameViewSetResolutionCommand : IGroupedCommand, IAsyncRpcCommand
    {
        private readonly IGameViewEnvironment environment;
        private readonly IGameViewSettleWaiter settler;

        public GameViewSetResolutionCommand(
            IGameViewEnvironment environment, IGameViewSettleWaiter settler)
        {
            this.environment = environment;
            this.settler = settler;
        }

        public string Method => RpcMethods.GameViewSetResolution;
        public string Group => RpcMethods.GameViewGroup;
        public string Action => "set-resolution";
        public RpcToolDescriptor Descriptor => GameViewCommandSupport.Descriptor(
            "game-view-set-resolution", Method, "Game View / Set Resolution",
            "Select an existing Game View resolution by index from list-resolutions.",
            JsonRpcSerializer.Object(
                ("index", JsonRpcSerializer.Object(
                    ("type", "integer"),
                    ("minimum", 0),
                    ("description", "Resolution index returned by list-resolutions.")))),
            false);

        public JsonRpcResponse Handle(JsonRpcRequest request)
            => GameViewCommandSupport.AsyncOnly(request.Id);

        public async UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request)
        {
            if (!TryReadInt(request.Params, "index", out int index))
            {
                return GameViewCommandSupport.InvalidParams(
                    request.Id, "index is required and must be an integer.");
            }

            IReadOnlyList<GameViewResolutionInfo> resolutions = environment.ListResolutions();
            if (index < 0 || index >= resolutions.Count)
            {
                return GameViewCommandSupport.InvalidParams(
                    request.Id,
                    "Resolution index is outside the current Game View resolution list.",
                    "resolution_index_out_of_range");
            }

            var lookup = environment.FindTarget();
            if (!lookup.Ok)
            {
                return GameViewCommandSupport.TargetError(request.Id, lookup);
            }

            lookup.Target.SelectedResolutionIndex = index;
            lookup.Target.Repaint();
            bool settled = await settler.WaitAsync(lookup.Target);
            return JsonRpcResponse.FromSuccess(
                request.Id,
                GameViewCommandSupport.BuildState(
                    lookup.Target, environment.ListResolutions(), true, settled));
        }

        private static bool TryReadInt(JsonData data, string key, out int value)
        {
            if (data != null && data.IsObject &&
                data.ContainsKey(key) && data[key].IsInt)
            {
                value = (int)data[key];
                return true;
            }
            value = 0;
            return false;
        }
    }

    public sealed class GameViewSetMaximizedCommand : IGroupedCommand, IAsyncRpcCommand
    {
        private readonly IGameViewEnvironment environment;
        private readonly IGameViewSettleWaiter settler;

        public GameViewSetMaximizedCommand(
            IGameViewEnvironment environment, IGameViewSettleWaiter settler)
        {
            this.environment = environment;
            this.settler = settler;
        }

        public string Method => RpcMethods.GameViewSetMaximized;
        public string Group => RpcMethods.GameViewGroup;
        public string Action => "set-maximized";
        public RpcToolDescriptor Descriptor => GameViewCommandSupport.Descriptor(
            "game-view-set-maximized", Method, "Game View / Set Maximized",
            "Explicitly maximize or restore the current Game View window.",
            JsonRpcSerializer.Object(
                ("maximized", JsonRpcSerializer.Object(
                    ("type", "boolean"),
                    ("description", "true to maximize; false to restore.")))),
            false);

        public JsonRpcResponse Handle(JsonRpcRequest request)
            => GameViewCommandSupport.AsyncOnly(request.Id);

        public async UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request)
        {
            if (!TryReadBool(request.Params, "maximized", out bool maximized))
            {
                return GameViewCommandSupport.InvalidParams(
                    request.Id, "maximized is required and must be a boolean.");
            }

            var lookup = environment.FindTarget();
            if (!lookup.Ok)
            {
                return GameViewCommandSupport.TargetError(request.Id, lookup);
            }

            lookup.Target.Maximized = maximized;
            lookup.Target.Repaint();
            bool settled = await settler.WaitAsync(lookup.Target);
            return JsonRpcResponse.FromSuccess(
                request.Id,
                GameViewCommandSupport.BuildState(
                    lookup.Target, environment.ListResolutions(), true, settled));
        }

        private static bool TryReadBool(JsonData data, string key, out bool value)
        {
            if (data != null && data.IsObject &&
                data.ContainsKey(key) && data[key].IsBoolean)
            {
                value = (bool)data[key];
                return true;
            }
            value = false;
            return false;
        }
    }
}
