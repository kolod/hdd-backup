using System.Text;
using HddBackup.Core.Repair;
using Xunit;

namespace HddBackup.Core.Tests.Repair
{
    /// <summary>Feature 011 (research R-8): a public key is parsed, its fingerprint equals ssh-keygen's, anything else is refused.</summary>
    public class PublicKeyTests
    {
        // Made with ssh-keygen (OpenSSH 9.2) in the fixture container; the fingerprints are what "ssh-keygen -lf" printed.
        public const string Ed25519 = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAHaKLvoJyA2RZ2jzSFIdM9iJnmTRxf2YuWcDDD2Kcoe ed@host";
        private const string Ed25519Print = "SHA256:44sKSrQlFKO0RPrPZy/ls62I5K6b7NJKrFNPkVtWlx8";
        private const string Rsa = "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQCivOEc6zxa8igodW8vGvF/x+Jcb9dxKLgA9TxzCoAxjLAdQ0JVcLGATu1bcdSWT9D4QHIBv/0m6TY29Cn9t+afJATeJz02CsOYtWNpH6VVx1ezGrntXaJbSkAbWUc9TbvjmyqtwhnxKgF/4RfRl9VwUXnIerz/jBOfcNImaJetQDr6aCGujKrYZyYMSz+vWXK/KIiMDTHZlddHWx+ZKRrRlBzo4yW261r6Amx7XHnhv/EHgpMXLoVabMBBrGrz4mied2dBfTVj9rLN/MhV6Mrqzet8MMOzTEZztCSI4OO2qRQQjTVh4qdtZKut0q2rYbBfB8jo1iv3kNEGTo1WeoGR rsa@host";
        private const string RsaPrint = "SHA256:mrBoi8YRjUA+KSytlVByvoE0L1/1/nTkzPIDuEXsnNE";
        private const string P256 = "ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBNPNrHFcChmLgbgkCk/+7DSs1EzZ6rgaC5lHhLQ27bLn5ao18G6G4+/TjydvTXjVixVYI/5vUxQ1aiPrs0DUajc= p256@host";
        private const string P256Print = "SHA256:GTRwshyofB3ikkR3hiuNMMGDxIZ3m1CDfRRDEKzqPzs";
        private const string P384 = "ecdsa-sha2-nistp384 AAAAE2VjZHNhLXNoYTItbmlzdHAzODQAAAAIbmlzdHAzODQAAABhBH7193QJA6k5xqdPClOjoBdNTJZtdw3GlfTOcT+nijjKWEbiJ83dc9gBuR9qB0iaqaVrOrlC50iJZouYHE1wZoOIhhCOCTBlLUPPPCF72+N+IHifSgYj1GO1/5apWixQdQ== p384@host";
        private const string P384Print = "SHA256:KS3VH4nYERScZESHe3A7sp0yW7NX50q7auY0eTNH4V0";
        private const string P521 = "ecdsa-sha2-nistp521 AAAAE2VjZHNhLXNoYTItbmlzdHA1MjEAAAAIbmlzdHA1MjEAAACFBABzV2c9U6YED5UzE0n1OOMwiw+uM9oR1NkDmlWu1bIYV8J9JcliC3DUxyoCRwAReykjmPPEJe14uutB3vAHlPDsfwCSqHFbSHn8DskgbAzH3GsIXDXS5zffAmDMeE+q1dtj+AJBf9VpuXkwWaazgpGLphuZWao4yRiJLqr6b5uy0fwbXQ== p521@host";
        private const string P521Print = "SHA256:t1oKcO0euGFLrv66+GxCC6UncuftVpVfWj9DSTTSNrE";
        private const string Cert = "ssh-ed25519-cert-v01@openssh.com AAAAIHNzaC1lZDI1NTE5LWNlcnQtdjAxQG9wZW5zc2guY29tAAAAIHDvHBR03ngvyxYVOYw304NK5xT10mSYzFrVR52631V9AAAAIAHaKLvoJyA2RZ2jzSFIdM9iJnmTRxf2YuWcDDD2KcoeAAAAAAAAAAAAAAABAAAAAmlkAAAA ed@host";

        [Theory]
        [InlineData(Ed25519, "ssh-ed25519", Ed25519Print, "ed@host")]
        [InlineData(Rsa, "ssh-rsa", RsaPrint, "rsa@host")]
        [InlineData(P256, "ecdsa-sha2-nistp256", P256Print, "p256@host")]
        [InlineData(P384, "ecdsa-sha2-nistp384", P384Print, "p384@host")]
        [InlineData(P521, "ecdsa-sha2-nistp521", P521Print, "p521@host")]
        public void EveryKindOfKeyIsParsedWithTheFingerprintOfSshKeygen(string line, string type, string print, string comment)
        {
            Assert.True(PublicKey.TryParse(line, out var key, out var refusal));
            Assert.Equal(KeyRefusal.None, refusal);
            Assert.Equal(type, key.Type);
            Assert.Equal(print, key.Fingerprint);
            Assert.Equal(comment, key.Comment);
            Assert.Equal(string.Empty, key.Options);
            Assert.Equal(KeyWarnings.None, key.Warnings);
            Assert.Equal(line, key.Line);
        }

        [Fact]
        public void TrailingLineBreaksAndSurroundingSpacesAreDroppedAndAKeyWithoutACommentWorks()
        {
            Assert.True(PublicKey.TryParse("  " + Ed25519 + "\r\n", out var key, out _));
            Assert.Equal(Ed25519, key.Line);
            string bare = Ed25519.Substring(0, Ed25519.LastIndexOf(' '));
            Assert.True(PublicKey.TryParse(bare, out var noComment, out _));
            Assert.Equal(string.Empty, noComment.Comment);
            Assert.Equal(bare, noComment.Line);
        }

        [Fact]
        public void ACommentWithSpacesIsKeptWhole()
        {
            Assert.True(PublicKey.TryParse(Ed25519 + " work laptop 2026", out var key, out _));
            Assert.Equal("ed@host work laptop 2026", key.Comment);
        }

        [Fact]
        public void OptionsAreRecognisedKeptAndWarnedAbout()
        {
            string line = "from=\"10.0.0.0/8,*.example.com\",command=\"/bin/echo hello world\",no-pty " + Ed25519;
            Assert.True(PublicKey.TryParse(line, out var key, out _));
            Assert.Equal("from=\"10.0.0.0/8,*.example.com\",command=\"/bin/echo hello world\",no-pty", key.Options);
            Assert.Equal("ssh-ed25519", key.Type);
            Assert.True(key.Warnings.HasFlag(KeyWarnings.HasOptions));
            Assert.Equal(Ed25519Print, key.Fingerprint);
            Assert.Equal(line, key.Line);
        }

        [Fact]
        public void ALegacyDsaKeyIsAcceptedWithAWarning()
        {
            // a well-formed ssh-dss blob: the type string, then three short mpints (the content is not interpreted)
            var blob = new System.IO.MemoryStream();
            foreach (var part in new[] { "ssh-dss", "\u0001\u0002\u0003", "\u0004\u0005", "\u0006\u0007" })
            {
                var bytes = Encoding.ASCII.GetBytes(part);
                blob.Write(new byte[] { 0, 0, 0, (byte)bytes.Length }, 0, 4);
                blob.Write(bytes, 0, bytes.Length);
            }

            string line = "ssh-dss " + System.Convert.ToBase64String(blob.ToArray()) + " old";
            Assert.True(PublicKey.TryParse(line, out var key, out _));
            Assert.True(key.Warnings.HasFlag(KeyWarnings.LegacyType));
        }

        [Theory]
        [InlineData("", KeyRefusal.Empty)]
        [InlineData("   \r\n", KeyRefusal.Empty)]
        [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----\n", KeyRefusal.PrivateKey)]
        [InlineData("-----BEGIN RSA PRIVATE KEY-----", KeyRefusal.PrivateKey)]
        [InlineData("PuTTY-User-Key-File-3: ssh-ed25519\nEncryption: none", KeyRefusal.PrivateKey)]
        [InlineData(Cert, KeyRefusal.Certificate)]
        [InlineData("ssh-ed25519", KeyRefusal.UnknownType)]
        [InlineData("hunter2 hunter2 hunter2", KeyRefusal.UnknownType)]
        [InlineData("ssh-ed25519 !!!notbase64!!! comment", KeyRefusal.BadEncoding)]
        [InlineData("ssh-ed25519 AAAA comment", KeyRefusal.BadEncoding)]
        [InlineData("ssh-ed25519 ", KeyRefusal.UnknownType)]
        [InlineData("command=\"unclosed ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAHaKLvoJyA2RZ2jzSFIdM9iJnmTRxf2YuWcDDD2Kcoe", KeyRefusal.BadEncoding)]
        public void WhatIsNotOnePublicKeyIsRefusedWithAReason(string text, KeyRefusal expected)
        {
            Assert.False(PublicKey.TryParse(text, out var key, out var refusal));
            Assert.Null(key);
            Assert.Equal(expected, refusal);
        }

        [Fact]
        public void TwoLinesAreRefusedEvenWhenBothAreGood()
        {
            Assert.False(PublicKey.TryParse(Ed25519 + "\n" + Rsa, out _, out var refusal));
            Assert.Equal(KeyRefusal.Several, refusal);
        }

        [Fact]
        public void ATypeThatDoesNotMatchTheKeyDataIsRefused()
        {
            string wrong = "ssh-rsa " + Ed25519.Split(' ')[1] + " x";
            Assert.False(PublicKey.TryParse(wrong, out _, out var refusal));
            Assert.Equal(KeyRefusal.TypeMismatch, refusal);
        }

        [Fact]
        public void ATextOverSixteenKilobytesIsRefused()
        {
            Assert.False(PublicKey.TryParse(Ed25519 + " " + new string('x', PublicKey.MaxLength), out _, out var refusal));
            Assert.Equal(KeyRefusal.TooLong, refusal);
        }

        [Fact]
        public void TheSameKeyIsFoundInAFileWhateverItsCommentOrOptions()
        {
            Assert.True(PublicKey.TryParse(Ed25519, out var key, out _));
            string file = "# my keys\r\n\r\ncommand=\"/bin/true\" " + Ed25519.Replace("ed@host", "other-name") + "\r\n" + Rsa + "\r\n";
            Assert.True(key.IsIn(file));
            Assert.False(key.IsIn("# nothing\n" + Rsa + "\n"));
            Assert.False(key.IsIn(string.Empty));
            Assert.False(key.IsIn(null));
        }

        [Fact]
        public void ALineThatLooksLikeAKeyButDiffersInOneCharacterIsADifferentKey()
        {
            Assert.True(PublicKey.TryParse(Ed25519, out var key, out _));
            string other = Ed25519.Replace("AAAAIAHaKLvo", "AAAAIAHaKLvp");
            Assert.False(key.IsIn(other));
        }
    }
}
