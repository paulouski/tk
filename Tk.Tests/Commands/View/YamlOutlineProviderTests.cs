using Tk.Commands.View;
using Xunit;

namespace Tk.Tests.Commands.View;

public class YamlOutlineProviderTests
{
    private static string WriteTemp(string fileName, string content, string? subDir = null)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        if (subDir is not null)
        {
            dir = Path.Combine(dir, subDir);
            Directory.CreateDirectory(dir);
        }
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    // ─── CanHandle ────────────────────────────────────────────────────────────

    [Fact]
    public void CanHandle_accepts_yml_and_yaml_extensions_case_insensitive()
    {
        var provider = new YamlOutlineProvider();
        Assert.True(provider.CanHandle("/foo/bar.yml"));
        Assert.True(provider.CanHandle("/foo/bar.YML"));
        Assert.True(provider.CanHandle("/foo/bar.yaml"));
        Assert.True(provider.CanHandle("/foo/bar.YAML"));
    }

    [Fact]
    public void CanHandle_rejects_non_yaml_extensions()
    {
        var provider = new YamlOutlineProvider();
        Assert.False(provider.CanHandle("/foo/bar.cs"));
        Assert.False(provider.CanHandle("/foo/bar.md"));
        Assert.False(provider.CanHandle("/foo/bar.json"));
        Assert.False(provider.CanHandle("/foo/bar"));
    }

    // ─── workflow file shape ──────────────────────────────────────────────────

    [Fact]
    public async Task Workflow_file_extracts_name_on_and_jobs_with_step_counts()
    {
        // Three jobs with three different step counts to verify the counter handles
        // varying step list lengths and the job-range finder stops at the next job.
        var content = string.Join('\n', new[]
        {
            "name: CI",
            "on: push",
            "jobs:",
            "  build:",
            "    runs-on: ubuntu-latest",
            "    steps:",
            "      - uses: actions/checkout@v4",
            "      - uses: actions/setup-dotnet@v4",
            "      - run: dotnet build",
            "      - run: dotnet test",
            "      - run: dotnet publish",
            "  test:",
            "    runs-on: ubuntu-latest",
            "    steps:",
            "      - uses: actions/checkout@v4",
            "      - run: dotnet test",
            "  deploy:",
            "    runs-on: ubuntu-latest",
            "    steps:",
            "      - run: echo deploy",
            "      - run: echo step2",
        });
        var path = WriteTemp("ci.yml", content, ".github/workflows");

        var provider = new YamlOutlineProvider();
        var result = await provider.GetOutlineAsync(path, default);

        Assert.NotNull(result);
        Assert.Equal("yaml", result!.Source);

        var meta = result.Entries.Single(e => e.Kind == "meta");
        Assert.Equal("CI", meta.Name);

        var on = result.Entries.Single(e => e.Kind == "on");
        Assert.Equal("push", on.Name);

        var section = result.Entries.Single(e => e.Kind == "section");
        Assert.Equal("jobs", section.Name);

        var jobs = result.Entries.Where(e => e.Kind == "job").ToList();
        Assert.Equal(3, jobs.Count);
        Assert.Equal("build", jobs[0].Name);
        Assert.Equal("5", jobs[0].Detail);
        Assert.Equal("test", jobs[1].Name);
        Assert.Equal("2", jobs[1].Detail);
        Assert.Equal("deploy", jobs[2].Name);
        Assert.Equal("2", jobs[2].Detail);
    }

    [Fact]
    public async Task Workflow_file_job_line_ranges_span_from_job_key_to_next_job()
    {
        // The build job spans lines 4-13 (next job at line 14). The end line is
        // inclusive of the last content line of the job, not the blank padding.
        var content = string.Join('\n', new[]
        {
            "name: CI",
            "on: push",
            "jobs:",
            "  build:",                            // 4
            "    runs-on: ubuntu-latest",          // 5
            "    steps:",                          // 6
            "      - run: echo a",                 // 7
            "      - run: echo b",                 // 8
            "      - run: echo c",                 // 9
            "    env:",                            // 10
            "      FOO: bar",                      // 11
            "      BAZ: qux",                      // 12
            "",                                    // 13
            "  test:",                             // 14
            "    runs-on: ubuntu-latest",
            "    steps:",
            "      - run: echo t",
        });
        var path = WriteTemp("ci.yml", content, ".github/workflows");

        var provider = new YamlOutlineProvider();
        var result = await provider.GetOutlineAsync(path, default);

        var jobs = result!.Entries.Where(e => e.Kind == "job").ToList();
        Assert.Equal("build", jobs[0].Name);
        Assert.Equal(4, jobs[0].StartLine);
        Assert.Equal(13, jobs[0].EndLine);
        Assert.Equal("test", jobs[1].Name);
        Assert.Equal(14, jobs[1].StartLine);
        Assert.Equal(17, jobs[1].EndLine);
    }

    [Fact]
    public async Task Workflow_file_with_quoted_on_key_is_recognized()
    {
        // YAML 1.1 treats `on` as a boolean keyword, so some workflows quote it.
        // The provider should accept both forms.
        var content = string.Join('\n', new[]
        {
            "name: CI",
            "\"on\":",
            "  push:",
            "  pull_request:",
            "jobs:",
            "  build:",
            "    runs-on: ubuntu-latest",
            "    steps:",
            "      - run: echo hi",
        });
        var path = WriteTemp("ci.yml", content);

        var provider = new YamlOutlineProvider();
        var result = await provider.GetOutlineAsync(path, default);

        var on = result!.Entries.Single(e => e.Kind == "on");
        Assert.Equal("push, pull_request", on.Name);
    }

    [Fact]
    public async Task On_with_inline_array_extracts_event_names()
    {
        var content = string.Join('\n', new[]
        {
            "name: CI",
            "on: [push, pull_request, workflow_dispatch]",
            "jobs:",
            "  build:",
            "    runs-on: ubuntu-latest",
            "    steps:",
            "      - run: echo hi",
        });
        var path = WriteTemp("ci.yml", content);

        var provider = new YamlOutlineProvider();
        var result = await provider.GetOutlineAsync(path, default);

        var on = result!.Entries.Single(e => e.Kind == "on");
        Assert.Equal("push, pull_request, workflow_dispatch", on.Name);
    }

    [Fact]
    public async Task On_with_inline_object_extracts_event_names()
    {
        var content = string.Join('\n', new[]
        {
            "name: CI",
            "on: { push: { branches: [main] }, pull_request: { branches: [main] } }",
            "jobs:",
            "  build:",
            "    runs-on: ubuntu-latest",
            "    steps:",
            "      - run: echo hi",
        });
        var path = WriteTemp("ci.yml", content);

        var provider = new YamlOutlineProvider();
        var result = await provider.GetOutlineAsync(path, default);

        var on = result!.Entries.Single(e => e.Kind == "on");
        Assert.Equal("push, pull_request", on.Name);
    }

    [Fact]
    public async Task Workflow_without_github_workflows_path_is_recognized_via_jobs_key()
    {
        // Structural fallback: a top-level `jobs:` is the second workflow signal.
        var content = string.Join('\n', new[]
        {
            "name: CI",
            "on: push",
            "jobs:",
            "  build:",
            "    runs-on: ubuntu-latest",
            "    steps:",
            "      - run: echo hi",
        });
        var path = WriteTemp("random-name.yml", content);

        var provider = new YamlOutlineProvider();
        var result = await provider.GetOutlineAsync(path, default);

        Assert.NotNull(result);
        Assert.Contains(result!.Entries, e => e.Kind == "section" && e.Name == "jobs");
        Assert.Contains(result.Entries, e => e.Kind == "job" && e.Name == "build");
    }

    // ─── generic YAML shape ───────────────────────────────────────────────────

    [Fact]
    public async Task Generic_yaml_returns_top_level_keys_with_ranges()
    {
        var content = string.Join('\n', new[]
        {
            "apiVersion: v1",                  // 1
            "kind: ConfigMap",                 // 2
            "metadata:",                       // 3
            "  name: foo",                     // 4
            "  namespace: bar",                // 5
            "data:",                           // 6
            "  key1: value1",                  // 7
            "  key2: value2",                  // 8
        });
        var path = WriteTemp("config.yaml", content);

        var provider = new YamlOutlineProvider();
        var result = await provider.GetOutlineAsync(path, default);

        Assert.NotNull(result);
        Assert.Equal("yaml", result!.Source);

        var keys = result.Entries.ToList();
        Assert.Equal(4, keys.Count);
        Assert.Equal("apiVersion", keys[0].Name);
        Assert.Equal(1, keys[0].StartLine);
        Assert.Equal("kind", keys[1].Name);
        Assert.Equal(2, keys[1].StartLine);
        Assert.Equal("metadata", keys[2].Name);
        Assert.Equal(3, keys[2].StartLine);
        Assert.Equal("data", keys[3].Name);
        Assert.Equal(6, keys[3].StartLine);
    }

    // ─── edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Empty_yaml_file_returns_empty_entries_with_yaml_source()
    {
        var path = WriteTemp("empty.yml", "");

        var provider = new YamlOutlineProvider();
        var result = await provider.GetOutlineAsync(path, default);

        Assert.NotNull(result);
        Assert.Equal("yaml", result!.Source);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task Minimal_workflow_without_steps_yields_zero_step_count()
    {
        // A job that has no `steps:` block (e.g. a reusable-workflow `uses:` call) should
        // produce a step count of 0, not throw or fall through to the regex provider.
        var content = string.Join('\n', new[]
        {
            "name: Tiny",
            "on: push",
            "jobs:",
            "  build:",
            "    uses: ./reusable-workflow.yml",
        });
        var path = WriteTemp("tiny.yml", content, ".github/workflows");

        var provider = new YamlOutlineProvider();
        var result = await provider.GetOutlineAsync(path, default);

        Assert.NotNull(result);
        var jobs = result!.Entries.Where(e => e.Kind == "job").ToList();
        Assert.Single(jobs);
        Assert.Equal("build", jobs[0].Name);
        Assert.Equal("0", jobs[0].Detail);
    }

    [Fact]
    public async Task Workflow_without_name_or_on_still_renders_jobs_section()
    {
        // Some workflows omit `name:` and use only `on:`. The provider should still
        // surface the jobs section rather than failing the whole query.
        var content = string.Join('\n', new[]
        {
            "jobs:",
            "  build:",
            "    runs-on: ubuntu-latest",
            "    steps:",
            "      - run: echo hi",
        });
        var path = WriteTemp("bare.yml", content);

        var provider = new YamlOutlineProvider();
        var result = await provider.GetOutlineAsync(path, default);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Entries, e => e.Kind == "meta");
        Assert.DoesNotContain(result.Entries, e => e.Kind == "on");
        Assert.Contains(result.Entries, e => e.Kind == "section" && e.Name == "jobs");
        var job = result.Entries.Single(e => e.Kind == "job");
        Assert.Equal("build", job.Name);
        Assert.Equal("1", job.Detail);
    }

    [Fact]
    public async Task Step_count_ignores_sub_list_items_at_deeper_indent()
    {
        // A step with a nested `with:` mapping (e.g. `with: { matrix: { os: [ubuntu, mac] } }`)
        // must not inflate the step count with the matrix entries.
        var content = string.Join('\n', new[]
        {
            "name: CI",
            "on: push",
            "jobs:",
            "  build:",
            "    runs-on: ubuntu-latest",
            "    steps:",
            "      - name: build",
            "        run: dotnet build",
            "      - name: test",
            "        run: dotnet test",
            "        with:",
            "          matrix:",
            "            os:",
            "              - ubuntu-latest",
            "              - macos-latest",
            "              - windows-latest",
        });
        var path = WriteTemp("ci.yml", content, ".github/workflows");

        var provider = new YamlOutlineProvider();
        var result = await provider.GetOutlineAsync(path, default);

        var job = result!.Entries.Single(e => e.Kind == "job");
        // Only the two real `- name:` items count; the matrix sub-items are at
        // a deeper indent and must be excluded.
        Assert.Equal("2", job.Detail);
    }
}
