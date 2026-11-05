using HddBackup.Core.Diagnostics;
using Xunit;

namespace HddBackup.Core.Tests.Diagnostics
{
    public class WildcardMatcherTests
    {
        [Theory]
        [InlineData("*.conf", "50-x.conf", true)]
        [InlineData("*.conf", "50-x.conf.bak", false)]
        [InlineData("*.conf", ".conf", true)]
        [InlineData("50-?.conf", "50-a.conf", true)]
        [InlineData("50-?.conf", "50-ab.conf", false)]
        [InlineData("[0-9]*", "10-foo", true)]
        [InlineData("[0-9]*", "foo", false)]
        [InlineData("[!a]x", "bx", true)]
        [InlineData("[!a]x", "ax", false)]
        [InlineData("[abc]", "b", true)]
        [InlineData("[abc]", "d", false)]
        [InlineData("a*b*c", "aXXbYYc", true)]
        [InlineData("a*b*c", "aXXbYY", false)]
        [InlineData("*", "anything", true)]
        [InlineData("*", "a/b", false)]
        [InlineData("a?b", "a/b", false)]
        [InlineData("", "", true)]
        [InlineData("", "x", false)]
        [InlineData("x", "", false)]
        [InlineData("[", "[", true)]
        [InlineData("a.b", "aXb", false)]
        [InlineData("a\\*", "a\\x", true)]
        [InlineData("**", "abc", true)]
        public void Matches(string pattern, string text, bool expected)
        {
            Assert.Equal(expected, WildcardMatcher.IsMatch(pattern, text));
        }
    }
}
