using System;
using System.Runtime.InteropServices;

namespace VsMcp
{
    /// <summary>
    /// COM RCW 的确定性释放辅助——C# 里模拟 C++ 栈对象 RAII。
    ///
    /// CLR 只在 GC 回收 RCW 时才 Release native 引用，时机不确定；codestore 关闭 solution
    /// 时会等所有 query session 引用归零，GC 没及时跑就死锁（关 VS hang——见
    /// VcTypeHierarchySearcher / VcSymbolSearcher 的历史 bug）。本类用 using 块把「块结束
    /// （含异常展开）先调 onDispose（通常是 Close）、再 Marshal.ReleaseComObject」固化下来，
    /// 确定性归零，省去每个调用点手写 try/finally + Release，也杜绝遗漏。
    /// </summary>
    internal static class Com
    {
        /// <summary>包住一个 COM 对象供 using 块内使用：块结束时先调 <paramref name="onDispose"/>
        /// 再 ReleaseComObject。<paramref name="value"/> 即 <paramref name="obj"/>，供块内直接用。
        /// 典型：<code>using (Com.Use(svc.Foo(), x => x.Close(), out var foo)) { foo.Bar(); }</code></summary>
        public static IDisposable Use<T>(T obj, Action<T>? onDispose, out T value) where T : class
        {
            value = obj;
            return new Releaser<T>(obj, onDispose);
        }

        /// <summary>对象已是变量、只需绑定释放（不返回 value）——用于方法级持有。
        /// 典型：<code>using var _ = Com.Use(qcs);</code> 整个方法结束才释放。</summary>
        public static IDisposable Use<T>(T obj, Action<T>? onDispose = null) where T : class
            => new Releaser<T>(obj, onDispose);

        private sealed class Releaser<T> : IDisposable where T : class
        {
            private T? _obj;
            private readonly Action<T>? _onDispose;
            internal Releaser(T obj, Action<T>? onDispose) { _obj = obj; _onDispose = onDispose; }
            public void Dispose()
            {
                var o = _obj; _obj = null;
                if (o == null) return;
                // 顺序：先 onDispose（通常是 Close，清内部资源）→ 再 Release RCW（减 native 引用）。
                // 不能反：Release 后 RCW 不可再用，Close 会失败/无效。
                try { _onDispose?.Invoke(o); } catch { }
                try { if (Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { }
            }
        }
    }
}
