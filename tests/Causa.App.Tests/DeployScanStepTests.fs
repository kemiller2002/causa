// Owner: integration (T20). The deploy workflow's "Scan the site for credentials" step, run as GitHub runs it
// (bash -e -o pipefail), taken verbatim from .github/workflows/deploy-pages.yml, against temporary dist/ fixtures
// (TEST-DEPLOY-KEY section 5). Guards the fix for run 42399862601: with only the deploy key in deploy-connection.json
// the step died silently, because grep's no-match exit failed the pipeline under pipefail.
namespace Causa.App.Tests

open System
open System.Diagnostics
open System.IO
open Xunit

module private ScanStep =
    let rec private root (dir: DirectoryInfo | null) =
        match dir with
        | null -> failwith "Causa.slnx not found above the test assembly"
        | d when File.Exists(Path.Combine(d.FullName, "Causa.slnx")) -> d.FullName
        | d -> root d.Parent

    let repo = root (DirectoryInfo AppContext.BaseDirectory)

    /// The step's `run: |` block, de-indented, exactly as committed.
    let script () =
        let lines = File.ReadAllLines(Path.Combine(repo, ".github/workflows/deploy-pages.yml"))
        let name = lines |> Array.findIndex (fun l -> l.Trim() = "- name: Scan the site for credentials")
        let run = lines |> Array.findIndex (fun l -> l.Trim() = "run: |") |> fun _ -> (lines |> Array.skip name |> Array.findIndex (fun l -> l.Trim() = "run: |")) + name
        let indent = lines[run + 1].Length - lines[run + 1].TrimStart().Length
        lines
        |> Array.skip (run + 1)
        |> Array.takeWhile (fun l -> l.Trim() = "" || l.Length - l.TrimStart().Length >= indent)
        |> Array.map (fun l -> if l.Length >= indent then l.Substring indent else "")
        |> String.concat "\n"

    /// The line as it was before the fix, to show the fix is what makes case 1 pass.
    let unfixed (s: string) =
        s.Replace(
            "others=$( { grep -oaE \"$pattern\" \"$config\" || true; } | { grep -vxF -- \"$DATA_ACCESS_KEY_CAUSA\" || true; } | wc -l)",
            "others=$(grep -oaE \"$pattern\" \"$config\" | grep -vxF -- \"$DATA_ACCESS_KEY_CAUSA\" | wc -l)"
        )

    // Synthetic key shapes only: they match the token pattern and are no one's credential.
    let key = "github_pat_" + String('A', 22) + "_" + String('b', 59)
    let other = "ghp_" + String('Z', 36)

    /// Runs the script under bash -e -o pipefail in a temporary directory holding `dist/` with the given files.
    let run (script: string) (files: (string * string) list) : int * string =
        let dir = Path.Combine(Path.GetTempPath(), "causa-scan-" + Guid.NewGuid().ToString "N")
        try
            for path, content in files do
                let full = Path.Combine(dir, "dist", path)
                Directory.CreateDirectory(Path.GetDirectoryName full |> Option.ofObj |> Option.defaultValue dir) |> ignore
                File.WriteAllText(full, content)
            let info = ProcessStartInfo("bash", [ "-e"; "-o"; "pipefail"; "-c"; script ], WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)
            info.Environment["DATA_ACCESS_KEY_CAUSA"] <- key
            use p = match Process.Start info with | null -> failwith "bash did not start" | p -> p
            let out = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()
            p.WaitForExit()
            p.ExitCode, out
        finally
            if Directory.Exists dir then Directory.Delete(dir, true)

    let config (keys: string list) =
        sprintf "{\"schema\":\"causa.deploy-connection/1\",\"connection\":{\"owner\":\"o\",\"repository\":\"r\",\"branch\":\"b\",\"key\":\"%s\"}}" (String.Join("\",\"other\":\"", keys))

type DeployScanStepTests() =
    [<Fact>]
    member _.``only the deploy key, in deploy-connection.json: the step passes``() =
        let code, out = ScanStep.run (ScanStep.script ()) [ "deploy-connection.json", ScanStep.config [ ScanStep.key ]; "index.html", "<!doctype html>" ]
        Assert.Equal(0, code)
        Assert.Contains("Credential scan: PASS", out)
        Assert.DoesNotContain(ScanStep.key, out)
        // Before the fix the same case ended the step with exit 1 and no message (run 42399862601).
        let before, beforeOut = ScanStep.run (ScanStep.unfixed (ScanStep.script ())) [ "deploy-connection.json", ScanStep.config [ ScanStep.key ] ]
        Assert.Equal(1, before)
        Assert.DoesNotContain("Credential scan: PASS", beforeOut)

    [<Fact>]
    member _.``the key plus another token in deploy-connection.json: the step fails``() =
        let code, out = ScanStep.run (ScanStep.script ()) [ "deploy-connection.json", ScanStep.config [ ScanStep.key; ScanStep.other ] ]
        Assert.Equal(1, code)
        Assert.Contains("a credential other than the deploy key", out)
        Assert.DoesNotContain(ScanStep.other, out)

    [<Fact>]
    member _.``the deploy key in another file: the step fails``() =
        let code, out = ScanStep.run (ScanStep.script ()) [ "deploy-connection.json", ScanStep.config [ ScanStep.key ]; "index.html", "<script>const k='" + ScanStep.key + "'</script>" ]
        Assert.Equal(1, code)
        Assert.Contains("the deploy key is outside", out)
        Assert.DoesNotContain(ScanStep.key, out)
