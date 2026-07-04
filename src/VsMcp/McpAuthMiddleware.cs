using System;
using System.Net;

namespace VsMcp
{
    /// <summary>
    /// MCP HTTP 服务器的 Bearer token 认证中间件。
    /// 针对 expected token 校验 Authorization 头。
    /// 当未配置 token（null）时，认证禁用，所有请求都放行。
    /// </summary>
    public sealed class McpAuthMiddleware
    {
        private readonly string? _expectedToken;

        /// <summary>
        /// 创建一个新的认证中间件实例。
        /// </summary>
        /// <param name="expectedToken">期望的 bearer token，为 null 则禁用认证。</param>
        public McpAuthMiddleware(string? expectedToken)
        {
            _expectedToken = expectedToken;
        }

        /// <summary>
        /// 获取认证是否启用（配置了 token 时为 true）。
        /// </summary>
        public bool IsEnabled => _expectedToken is not null;

        /// <summary>
        /// 校验传入 HTTP 请求的认证。
        /// 请求已授权则返回 true，否则 false。
        /// 认证禁用时（null token），所有请求都放行。
        /// </summary>
        /// <param name="request">要校验的传入 HTTP 请求。</param>
        /// <returns>请求通过认证则为 true，否则为 false。</returns>
        public bool ValidateRequest(HttpListenerRequest request)
        {
            return ValidateAuthHeader(request.Headers["Authorization"]);
        }

        /// <summary>
        /// 直接校验 Authorization 头的值。
        /// public 以便无需 HttpListenerRequest 即可做单元测试。
        /// </summary>
        /// <param name="authHeader">Authorization 头的值，或 null。</param>
        /// <returns>认证禁用或 bearer token 匹配则为 true。</returns>
        public bool ValidateAuthHeader(string? authHeader)
        {
            if (!IsEnabled)
                return true;

            if (authHeader is null || !authHeader.StartsWith("Bearer ", StringComparison.Ordinal))
                return false;

            var token = authHeader.Substring("Bearer ".Length).Trim();
            return string.Equals(token, _expectedToken, StringComparison.Ordinal);
        }
    }
}
