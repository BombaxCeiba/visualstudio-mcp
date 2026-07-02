using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace VsMcp.Logging
{
    /// <summary>
    /// Minimal <see cref="ILoggerFactory"/> implementation that routes all
    /// created loggers through a single <see cref="ILoggerProvider"/> with a
    /// configured minimum level.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This stands in for <c>Microsoft.Extensions.Logging.LoggerFactory</c>,
    /// which lives in the concrete <c>Microsoft.Extensions.Logging</c> NuGet
    /// package. That package is not currently referenced by the project and
    /// bringing it in is unnecessary for this extension's logging needs: the
    /// only consumer is <see cref="McpHttpServer"/> (which takes an
    /// <see cref="ILoggerFactory"/> and forwards it to the MCP SDK), and the
    /// only provider is <see cref="VsOutputWindowLoggerProvider"/>. A 20-line
    /// factory covers the contract with zero new dependencies.
    /// </para>
    /// <para>
    /// Created loggers cache the underlying provider logger by category name;
    /// <see cref="ILogger"/> instances from a single provider are stateless
    /// beyond the provider reference, so the cache is safe to share.
    /// </para>
    /// </remarks>
    internal sealed class SimpleLoggerFactory : ILoggerFactory
    {
        private readonly ILoggerProvider _provider;
        private readonly LogLevel _minimumLevel;
        private readonly Dictionary<string, ILogger> _loggers = new Dictionary<string, ILogger>(StringComparer.Ordinal);
        private readonly object _gate = new object();
        private bool _disposed;

        public SimpleLoggerFactory(ILoggerProvider provider, LogLevel minimumLevel)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _minimumLevel = minimumLevel;
        }

        public ILogger CreateLogger(string categoryName)
        {
            if (categoryName == null)
                throw new ArgumentNullException(nameof(categoryName));

            lock (_gate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(SimpleLoggerFactory));

                if (!_loggers.TryGetValue(categoryName, out var logger))
                {
                    logger = new MinimumLevelFilteringLogger(_provider.CreateLogger(categoryName), _minimumLevel);
                    _loggers[categoryName] = logger;
                }
                return logger;
            }
        }

        public void AddProvider(ILoggerProvider provider)
        {
            // Single-provider factory; additional providers are ignored. The
            // extension only ever wires one provider (VsOutputWindowLoggerProvider).
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _provider.Dispose();
                _loggers.Clear();
            }
        }

        /// <summary>
        /// Wraps a provider logger so that events below the configured minimum
        /// level are filtered before reaching the provider. The provider's own
        /// <c>IsEnabled</c> also gates at Information (D-07); this filter is
        /// the factory-level control surface so callers can tighten the level
        /// without touching the provider.
        /// </summary>
        private sealed class MinimumLevelFilteringLogger : ILogger
        {
            private readonly ILogger _inner;
            private readonly LogLevel _minimumLevel;

            internal MinimumLevelFilteringLogger(ILogger inner, LogLevel minimumLevel)
            {
                _inner = inner;
                _minimumLevel = minimumLevel;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                    _inner.Log(logLevel, eventId, state, exception, formatter);
            }

            public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel;

            public IDisposable BeginScope<TState>(TState state) where TState : notnull
                => _inner.BeginScope(state) ?? NullScope.Instance;
        }
    }
}
