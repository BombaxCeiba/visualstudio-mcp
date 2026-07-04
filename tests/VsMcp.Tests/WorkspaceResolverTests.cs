using System;
using System.IO;
using VsMcpGateway;
using Xunit;

namespace VsMcp.Tests
{
    /// <summary>
    /// ① Header 层文件夹级前缀匹配器的纯函数测试（设计文档 §①）。
    /// 零依赖 —— 无 pipe / 无 HTTP。
    /// </summary>
    public class WorkspaceResolverTests
    {
        // 解析器只消费 (Pid, SolutionDir) 元组，所以测试直接喂入元组，
        // 无需构造 PipeRouter/InstanceEntry。
        private static readonly (int, string?)[] One = new[] { (1234, (string?)"D:\\projects\\MyApp") };

        [Fact]
        public void ExactDirectoryMatch_ReturnsSingle()
        {
            var m = WorkspaceResolver.Resolve("D:\\projects\\MyApp", One);
            Assert.Equal(WorkspaceMatchKind.Single, m.Kind);
            Assert.Equal(1234, m.Pid);
            Assert.Single(m.MatchedPids);
        }

        [Fact]
        public void SubPathMatch_ReturnsSingle()
        {
            // Header 是 SolutionDir 的后代 → 匹配（设计文档 §①）。
            var m = WorkspaceResolver.Resolve("D:\\projects\\MyApp\\src", One);
            Assert.Equal(WorkspaceMatchKind.Single, m.Kind);
            Assert.Equal(1234, m.Pid);
        }

        [Fact]
        public void SlnFilePath_ExtractsDirectory_AndMatches()
        {
            // .sln header 等价于其所在文件夹。
            var m = WorkspaceResolver.Resolve("D:\\projects\\MyApp\\MyApp.sln", One);
            Assert.Equal(WorkspaceMatchKind.Single, m.Kind);
            Assert.Equal(1234, m.Pid);
        }

        [Fact]
        public void TrailingSeparator_Normalized_ToSingle()
        {
            var m = WorkspaceResolver.Resolve("D:\\projects\\MyApp\\", One);
            Assert.Equal(WorkspaceMatchKind.Single, m.Kind);
        }

        [Fact]
        public void ForwardSlashes_Normalized_ToSingle()
        {
            var m = WorkspaceResolver.Resolve("D:/projects/MyApp/src", One);
            Assert.Equal(WorkspaceMatchKind.Single, m.Kind);
        }

        [Fact]
        public void CaseInsensitive_MatchesSingle()
        {
            var m = WorkspaceResolver.Resolve("d:\\PROJECTS\\myapp", One);
            Assert.Equal(WorkspaceMatchKind.Single, m.Kind);
        }

        [Fact]
        public void BoundaryCheck_RejectsSiblingPrefix()
        {
            // D:\projects\MyApp 必须不匹配 D:\projects\MyAppOther —— 前缀后的
            // 字符是 'O'，不是分隔符（设计文档 boundary check）。
            var m = WorkspaceResolver.Resolve("D:\\projects\\MyAppOther", One);
            Assert.Equal(WorkspaceMatchKind.None, m.Kind);
        }

        [Fact]
        public void BoundaryCheck_RejectsPartialSegmentPrefix()
        {
            // SolutionDir "D:\MyApp" 对比 header "D:\MyAp"（更短，无完整段）
            // —— header 必须是 SolutionDir 或其后代，绝不能是祖先。
            var instances = new[] { (1, (string?)"D:\\MyApp") };
            var m = WorkspaceResolver.Resolve("D:\\MyAp", instances);
            Assert.Equal(WorkspaceMatchKind.None, m.Kind);
        }

        [Fact]
        public void NoMatch_ReturnsNone()
        {
            var m = WorkspaceResolver.Resolve("D:\\projects\\Other", One);
            Assert.Equal(WorkspaceMatchKind.None, m.Kind);
            Assert.Null(m.Pid);
            Assert.Empty(m.MatchedPids);
        }

        [Fact]
        public void MultipleMatches_ReturnsAmbiguous()
        {
            var instances = new[]
            {
                (1234, (string?)"D:\\projects\\MyApp"),
                (5678, (string?)"D:\\projects\\MyApp\\sub"), // 后代也匹配 header 目录
            };
            // Header = D:\projects\MyApp。两个 SolutionDir 都等于或在它之下：
            // 1234 精确，5678 是……等等 5678 的目录在 header 之下，所以
            // header 不是 5678 目录的前缀（header 更短）。规则是"header 是
            // SolutionDir 或后代"——5678 目录更深意味着 header != 目录且 header
            // 不是后代。歧义需要两个 SolutionDir 都包含 header。
            // 改用下面的用例：
            var instances2 = new[]
            {
                (1234, (string?)"D:\\projects"),          // header D:\projects\MyApp 是子目录
                (5678, (string?)"D:\\projects\\MyApp"),   // header == 目录
            };
            var m = WorkspaceResolver.Resolve("D:\\projects\\MyApp", instances2);
            Assert.Equal(WorkspaceMatchKind.Ambiguous, m.Kind);
            Assert.Null(m.Pid);
            Assert.Equal(2, m.MatchedPids.Count);
            Assert.Contains(1234, m.MatchedPids);
            Assert.Contains(5678, m.MatchedPids);

            // 校验：第一组 "instances"（一个精确、一个更深）结果是 Single。
            var single = WorkspaceResolver.Resolve("D:\\projects\\MyApp", instances);
            Assert.Equal(WorkspaceMatchKind.Single, single.Kind);
        }

        [Fact]
        public void NullSolutionDir_Skipped()
        {
            var instances = new[]
            {
                (1234, (string?)null),                 // 未打开 solution —— 不可匹配
                (1234, (string?)""),                    // 空字符串同上
                (9999, (string?)"D:\\projects\\MyApp"),
            };
            var m = WorkspaceResolver.Resolve("D:\\projects\\MyApp", instances);
            Assert.Equal(WorkspaceMatchKind.Single, m.Kind);
            Assert.Equal(9999, m.Pid);
        }

        [Fact]
        public void EmptyHeader_ReturnsNone()
        {
            Assert.Equal(WorkspaceMatchKind.None, WorkspaceResolver.Resolve("", One).Kind);
            Assert.Equal(WorkspaceMatchKind.None, WorkspaceResolver.Resolve("   ", One).Kind);
            Assert.Equal(WorkspaceMatchKind.None, WorkspaceResolver.Resolve(null, One).Kind);
        }
    }
}
