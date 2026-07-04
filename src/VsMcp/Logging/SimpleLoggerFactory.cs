using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace VsMcp.Logging
{
    /// <summary>
    /// 最小化的 <see cref="ILoggerFactory"/> 实现，把所有创建的 logger 经单一
    /// <see cref="ILoggerProvider"/> 路由，并带一个配置的最小级别。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 它代替 <c>Microsoft.Extensions.Logging.LoggerFactory</c>——后者位于
    /// 具体的 <c>Microsoft.Extensions.Logging</c> NuGet 包中。该项目当前未引用
    /// 该包，且为本扩展的日志需求引入它没必要：消费者是 VS package +
    /// PipeMcpServer + GatewayLauncher（每个都接收 <see cref="ILoggerFactory"/>
    /// 并转发给 MCP SDK），唯一 provider 是
    /// <see cref="VsOutputWindowLoggerProvider"/>。20 行的工厂以零新依赖覆盖该
    /// 契约。
    /// </para>
    /// <para>
    /// 创建的 logger 按 category 名缓存底层 provider logger；来自单一 provider
    /// 的 <see cref="ILogger"/> 实例除 provider 引用外无状态，故缓存共享是
    /// 安全的。
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
            // 单 provider 工厂；额外的 provider 被忽略。本扩展只接一个
            // provider（VsOutputWindowLoggerProvider）。
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
        /// 包装一个 provider logger，使低于所配置最小级别的事件在到达 provider
        /// 前被过滤。provider 自身的 <c>IsEnabled</c> 也在 Information 处把关
        /// （D-07）；该过滤器是工厂级控制面，使调用方无需改 provider 即可收紧
        /// 级别。
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
