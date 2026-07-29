using System.Collections.Generic;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Testing
{
    // 结果模型 -> JsonData。纯函数, 无状态。
    // 裁剪存在的原因: 本仓库 331 个用例的全量 NUnit 结果是 232 KB,
    // 折 JSON 约 5 万 token 量级, 默认载荷必须瘦。
    public static class TestResultPayload
    {
        public const int MaxFailures = 50;
        public const int MaxMessageChars = 1000;
        public const int MaxStackTraceChars = 2000;

        public static JsonData BuildRunResult(TestRunOutcome outcome, bool includeDetails)
        {
            var summary = outcome?.Summary ?? new TestRunSummary();
            var payload = JsonRpcSerializer.Object(
                ("summary", JsonRpcSerializer.Object(
                    ("total", summary.Total),
                    ("passed", summary.Passed),
                    ("failed", summary.Failed),
                    ("skipped", summary.Skipped),
                    ("durationSeconds", summary.DurationSeconds),
                    ("resultState", summary.ResultState ?? "Unknown"))),
                ("domainReloadDisabled", outcome != null && outcome.DomainReloadDisabled),
                ("resumedAcrossReload", outcome != null && outcome.ResumedAcrossReload));

            var results = outcome?.Results ?? new List<TestCaseResult>();
            payload["failures"] = BuildFailures(results, out bool capped);
            payload["failuresCapped"] = capped;

            if (includeDetails)
            {
                var all = new JsonData();
                all.SetJsonType(JsonType.Array);
                foreach (var result in results)
                {
                    all.Add(BuildCase(result, includeState: true));
                }

                payload["results"] = all;
            }

            return payload;
        }

        // 失败判据: 非 Passed 且非 Skipped。Skipped/Inconclusive 不算失败,
        // 也不进 failures 列表 —— 它们不携带诊断价值, 只会白耗上下文。
        public static JsonData BuildFailures(IReadOnlyList<TestCaseResult> results, out bool capped)
        {
            var failures = new JsonData();
            failures.SetJsonType(JsonType.Array);
            capped = false;

            if (results == null)
            {
                return failures;
            }

            foreach (var result in results)
            {
                if (!IsFailure(result))
                {
                    continue;
                }

                if (failures.Count >= MaxFailures)
                {
                    capped = true;
                    break;
                }

                failures.Add(BuildCase(result, includeState: false));
            }

            return failures;
        }

        public static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max)
            {
                return value;
            }

            return value.Substring(0, max);
        }

        private static bool IsFailure(TestCaseResult result)
        {
            if (result == null || string.IsNullOrEmpty(result.State))
            {
                return false;
            }

            return result.State != "Passed" && result.State != "Skipped";
        }

        private static JsonData BuildCase(TestCaseResult result, bool includeState)
        {
            var data = JsonRpcSerializer.Object(("fullName", result?.FullName ?? string.Empty));
            if (includeState)
            {
                data["state"] = result?.State ?? "Unknown";
                data["durationSeconds"] = result?.DurationSeconds ?? 0d;
            }

            string message = Truncate(result?.Message, MaxMessageChars);
            if (!string.IsNullOrEmpty(message))
            {
                data["message"] = message;
            }

            string stackTrace = Truncate(result?.StackTrace, MaxStackTraceChars);
            if (!string.IsNullOrEmpty(stackTrace))
            {
                data["stackTrace"] = stackTrace;
            }

            return data;
        }
    }
}
