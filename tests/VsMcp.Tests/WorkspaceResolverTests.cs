using System;
using System.IO;
using VsMcpGateway;
using Xunit;

namespace VsMcp.Tests
{
    /// <summary>
    /// Pure-function tests for the ① Header tier's folder-level prefix matcher
    /// (设计文档 §①). Zero dependencies — no pipe / no HTTP.
    /// </summary>
    public class WorkspaceResolverTests
    {
        // (Pid, SolutionDir) tuples are the only shape the resolver consumes, so
        // tests feed them directly without constructing PipeRouter/InstanceEntry.
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
            // Header is a descendant of SolutionDir → match (设计文档 §①).
            var m = WorkspaceResolver.Resolve("D:\\projects\\MyApp\\src", One);
            Assert.Equal(WorkspaceMatchKind.Single, m.Kind);
            Assert.Equal(1234, m.Pid);
        }

        [Fact]
        public void SlnFilePath_ExtractsDirectory_AndMatches()
        {
            // A .sln header is equivalent to its containing folder.
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
            // D:\projects\MyApp must NOT match D:\projects\MyAppOther — the char
            // after the prefix is 'O', not a separator (设计文档 boundary check).
            var m = WorkspaceResolver.Resolve("D:\\projects\\MyAppOther", One);
            Assert.Equal(WorkspaceMatchKind.None, m.Kind);
        }

        [Fact]
        public void BoundaryCheck_RejectsPartialSegmentPrefix()
        {
            // SolutionDir "D:\MyApp" vs header "D:\MyAp" (shorter, no full segment)
            // — header must be SolutionDir-or-descendant, never an ancestor.
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
                (5678, (string?)"D:\\projects\\MyApp\\sub"), // descendant also matches the header dir
            };
            // Header = D:\projects\MyApp. Both SolutionDirs are at-or-under it:
            // 1234 exact, 5678 is... wait 5678's dir is UNDER the header, so the
            // header is NOT a prefix of 5678's dir (the header is shorter). The
            // rule is "header is SolutionDir or a descendant" — so 5678's dir
            // being deeper means header != dir and header is not a descendant.
            // Ambiguity needs two SolutionDirs that BOTH contain the header.
            // Build that case instead:
            var instances2 = new[]
            {
                (1234, (string?)"D:\\projects"),          // header D:\projects\MyApp is a child
                (5678, (string?)"D:\\projects\\MyApp"),   // header == dir
            };
            var m = WorkspaceResolver.Resolve("D:\\projects\\MyApp", instances2);
            Assert.Equal(WorkspaceMatchKind.Ambiguous, m.Kind);
            Assert.Null(m.Pid);
            Assert.Equal(2, m.MatchedPids.Count);
            Assert.Contains(1234, m.MatchedPids);
            Assert.Contains(5678, m.MatchedPids);

            // Sanity: the first "instances" set (one exact, one deeper) is Single.
            var single = WorkspaceResolver.Resolve("D:\\projects\\MyApp", instances);
            Assert.Equal(WorkspaceMatchKind.Single, single.Kind);
        }

        [Fact]
        public void NullSolutionDir_Skipped()
        {
            var instances = new[]
            {
                (1234, (string?)null),                 // no solution open — not matchable
                (1234, (string?)""),                    // empty ditto
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
