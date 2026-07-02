using System;
using System.Net;

namespace VsMcp
{
    /// <summary>
    /// Bearer token authentication middleware for the MCP HTTP server.
    /// Validates Authorization headers against an expected token.
    /// When no token is configured (null), authentication is disabled and all requests are allowed.
    /// </summary>
    public sealed class McpAuthMiddleware
    {
        private readonly string? _expectedToken;

        /// <summary>
        /// Creates a new auth middleware instance.
        /// </summary>
        /// <param name="expectedToken">The expected bearer token, or null to disable authentication.</param>
        public McpAuthMiddleware(string? expectedToken)
        {
            _expectedToken = expectedToken;
        }

        /// <summary>
        /// Gets whether authentication is enabled (true when a token is configured).
        /// </summary>
        public bool IsEnabled => _expectedToken is not null;

        /// <summary>
        /// Validates the authentication of an incoming HTTP request.
        /// Returns true if the request is authorized, false otherwise.
        /// When auth is disabled (null token), all requests are allowed.
        /// </summary>
        /// <param name="request">The incoming HTTP request to validate.</param>
        /// <returns>True if the request passes authentication, false otherwise.</returns>
        public bool ValidateRequest(HttpListenerRequest request)
        {
            return ValidateAuthHeader(request.Headers["Authorization"]);
        }

        /// <summary>
        /// Validates an Authorization header value directly.
        /// Public for unit testing without requiring HttpListenerRequest.
        /// </summary>
        /// <param name="authHeader">The Authorization header value, or null.</param>
        /// <returns>True if auth is disabled or the bearer token matches.</returns>
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
