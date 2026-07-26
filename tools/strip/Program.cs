using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// Strips every comment out of a tree of C# files, in place.
//
// Local `master` is the comment-bearing truth; the public fork is a mechanically stripped mirror
// (docs/decisions.md locks "no comments in shipped source"). This tool is that mechanism. It lived in
// %TEMP% for weeks, which meant every release started by rewriting it from memory, so it is vendored
// here now — scripts/publish-mirror.ps1 builds and calls it against a staging copy, never the repo.
//
// Roslyn does the work rather than a regex: a comment marker inside a string or a verbatim path
// ("//server\share", @"a // b") is not a comment, and only a real parser knows the difference.
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: strip <directory>");
    return 2;
}

string root = args[0];
if (!Directory.Exists(root))
{
    Console.Error.WriteLine($"strip: no such directory: {root}");
    return 2;
}

int n = 0;
foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
{
    if (path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
        path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;

    string text = File.ReadAllText(path);
    var root_ = CSharpSyntaxTree.ParseText(text).GetRoot();
    string outText = new CommentRemover().Visit(root_)!.ToFullString();
    outText = Regex.Replace(outText, @"[ \t]+(\r?\n)", "$1");   // trailing ws left by a stripped comment
    outText = Regex.Replace(outText, @"(\r?\n){3,}", "$1$1");    // collapse blank runs to one

    if (outText != text) { File.WriteAllText(path, outText); n++; }
}
Console.WriteLine($"stripped {n} files under {root}");
return 0;

sealed class CommentRemover() : CSharpSyntaxRewriter(visitIntoStructuredTrivia: true)
{
    public override SyntaxTrivia VisitTrivia(SyntaxTrivia t) =>
        t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
        t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)
            ? default : base.VisitTrivia(t);
}
