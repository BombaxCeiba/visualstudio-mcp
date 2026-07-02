using Xunit;

namespace VsMcp.Tests
{
    public class AuthMiddlewareTests
    {
        [Fact]
        public void AuthDisabled_AllowsAllRequests()
        {
            var middleware = new McpAuthMiddleware(null);

            Assert.False(middleware.IsEnabled);
            Assert.True(middleware.ValidateAuthHeader(null));
            Assert.True(middleware.ValidateAuthHeader(""));
            Assert.True(middleware.ValidateAuthHeader("anything"));
            Assert.True(middleware.ValidateAuthHeader("Bearer some-token"));
        }

        [Fact]
        public void AuthEnabled_CorrectToken_ReturnsTrue()
        {
            var middleware = new McpAuthMiddleware("my-secret");

            Assert.True(middleware.IsEnabled);
            Assert.True(middleware.ValidateAuthHeader("Bearer my-secret"));
        }

        [Fact]
        public void AuthEnabled_MissingHeader_ReturnsFalse()
        {
            var middleware = new McpAuthMiddleware("my-secret");

            Assert.False(middleware.ValidateAuthHeader(null));
        }

        [Fact]
        public void AuthEnabled_WrongToken_ReturnsFalse()
        {
            var middleware = new McpAuthMiddleware("my-secret");

            Assert.False(middleware.ValidateAuthHeader("Bearer wrong"));
        }

        [Fact]
        public void AuthEnabled_MalformedHeader_ReturnsFalse()
        {
            var middleware = new McpAuthMiddleware("my-secret");

            Assert.False(middleware.ValidateAuthHeader("Basic abc"));
            Assert.False(middleware.ValidateAuthHeader("Bear my-secret"));
            Assert.False(middleware.ValidateAuthHeader("bearer my-secret"));
        }
    }
}
