using System;
using System.IO;
using NUnit.Framework;

namespace Ushell.Editor.Tests
{
    public sealed class UshellPathsTests
    {
        [Test]
        public void EnsureWriteAllowed_RejectsSiblingWithSharedPrefix()
        {
            string testRoot = Path.Combine(Path.GetTempPath(), "ushell-path-tests-" + Guid.NewGuid().ToString("N"));
            string allowedRoot = Path.Combine(testRoot, "allowed");
            string allowedFile = Path.Combine(allowedRoot, "result.txt");
            string siblingFile = Path.Combine(testRoot, "allowed-backup", "result.txt");
            UshellSettings settings = UshellSettings.Instance;
            string[] originalAllowedPaths = settings.AllowedPaths;
            bool originalConfirmation = settings.DangerousOperationRequireConfirm;

            try
            {
                settings.AllowedPaths = new[] { allowedRoot };
                settings.DangerousOperationRequireConfirm = false;

                Assert.That(UshellPaths.EnsureWriteAllowed(allowedFile, false, out string allowedError), Is.True, allowedError);
                Assert.That(UshellPaths.EnsureWriteAllowed(siblingFile, false, out string siblingError), Is.False);
                Assert.That(siblingError, Does.Contain("outside the configured allowed paths"));
            }
            finally
            {
                settings.AllowedPaths = originalAllowedPaths;
                settings.DangerousOperationRequireConfirm = originalConfirmation;
                if (Directory.Exists(testRoot))
                {
                    Directory.Delete(testRoot, true);
                }
            }
        }

        [Test]
        public void EnsureWriteAllowed_RejectsParentTraversal()
        {
            string testRoot = Path.Combine(Path.GetTempPath(), "ushell-path-tests-" + Guid.NewGuid().ToString("N"));
            string allowedRoot = Path.Combine(testRoot, "allowed");
            string traversedFile = Path.Combine(allowedRoot, "..", "outside", "result.txt");
            UshellSettings settings = UshellSettings.Instance;
            string[] originalAllowedPaths = settings.AllowedPaths;
            bool originalConfirmation = settings.DangerousOperationRequireConfirm;

            try
            {
                settings.AllowedPaths = new[] { allowedRoot };
                settings.DangerousOperationRequireConfirm = false;

                Assert.That(UshellPaths.EnsureWriteAllowed(traversedFile, false, out string error), Is.False);
                Assert.That(error, Does.Contain("outside the configured allowed paths"));
            }
            finally
            {
                settings.AllowedPaths = originalAllowedPaths;
                settings.DangerousOperationRequireConfirm = originalConfirmation;
                if (Directory.Exists(testRoot))
                {
                    Directory.Delete(testRoot, true);
                }
            }
        }
    }
}
