using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

/// <summary>Text put by on disk while a long read goes on (<see cref="TextSpill"/>).</summary>
[TestClass]
public class TextSpillTests
{
    private static string Folder() => Path.Combine(Path.GetTempPath(), "anyfs-tests", "put-by-" + Guid.NewGuid().ToString("N"));

    [TestMethod]
    public void WhatIsPutByComesBackAsItWasInAnyOrderAndAsOftenAsAsked()
    {
        var folder = Folder();
        try
        {
            string[] texts =
            [
                "<p>plain</p>",
                "",
                "<p>café, 日本語, and a pair: 😀</p>",
                new string('x', 200_000),
                "<p>the last</p>",
            ];
            using (var spill = new TextSpill(folder))
            {
                var kept = texts.Select(spill.Put).ToArray();
                Assert.AreEqual(1, Directory.GetFiles(folder).Length, "one file, however many pieces");
                Assert.AreEqual(default, kept[1], "nothing is written for nothing");
                Assert.AreEqual(texts.Sum(t => System.Text.Encoding.UTF8.GetByteCount(t)), spill.Bytes);
                foreach (var i in new[] { 4, 0, 3, 2, 1, 3, 0 })
                {
                    Assert.AreEqual(texts[i], spill.Take(kept[i]));
                }

                // put by after some was taken, and taken from many threads at once
                var more = spill.Put("<p>more</p>");
                Parallel.For(0, 200, n => Assert.AreEqual(texts[n % 5], spill.Take(kept[n % 5])));
                Assert.AreEqual("<p>more</p>", spill.Take(more));
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => spill.Take(new TextSpill.Kept(spill.Bytes - 2, 10)));
            }

            Assert.AreEqual(0, Directory.GetFiles(folder).Length, "gone when the read is over");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void WhatAKilledRunLeftIsSweptAndNothingElseInTheFolder()
    {
        var folder = Folder();
        try
        {
            TextSpill.Sweep(folder);
            Assert.IsFalse(Directory.Exists(folder), "a folder that is not there is not made to be swept");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "put-by-0123.tmp"), "left by a run that was killed");
            File.WriteAllText(Path.Combine(folder, "something-else.txt"), "not ours");
            using var open = new TextSpill(folder);
            open.Put("in use");
            TextSpill.Sweep(folder);
            Assert.IsFalse(File.Exists(Path.Combine(folder, "put-by-0123.tmp")), "the one left is gone");
            Assert.IsTrue(File.Exists(Path.Combine(folder, "something-else.txt")), "what is not ours stays");
            Assert.AreEqual("in use", open.Take(new TextSpill.Kept(0, 6)), "and one in use is as it was (where the system lets a file in use be deleted, its holder still reads it)");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
