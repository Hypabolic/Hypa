using Hypa.AgentRuntime.Domain.ProgramStatus;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ProgramStatusStoreTests
{
    private static ProgramStatusReport Report(
        ProgramStatusState state,
        string? msg = null,
        string? app = null,
        string? id = null,
        ProgramStatusKind? kind = null) =>
        new() { State = state, Msg = msg, App = app, Id = id, Kind = kind };

    [Fact]
    public void Report_creates_the_root_record_with_a_sequence()
    {
        var store = ProgramStatusStore.Empty.Apply(Report(ProgramStatusState.Working, "m", "brew"));
        var record = Assert.Single(store.Records);
        Assert.Null(record.Id);
        Assert.Equal(ProgramStatusState.Working, record.State);
        Assert.Equal("m", record.Msg);
        Assert.Equal("brew", record.App);
        Assert.Equal(1, record.UpdatedSeq);
    }

    [Fact]
    public void Report_replaces_the_record_completely()
    {
        var store = ProgramStatusStore.Empty
            .Apply(Report(ProgramStatusState.Blocked, "m", "brew", kind: ProgramStatusKind.Auth))
            .Apply(Report(ProgramStatusState.Working));
        var record = Assert.Single(store.Records);
        Assert.Equal(ProgramStatusState.Working, record.State);
        Assert.Null(record.Msg);
        Assert.Null(record.App);
        Assert.Null(record.Kind);
        Assert.Equal(2, record.UpdatedSeq);
    }

    [Fact]
    public void Identical_report_returns_the_same_store()
    {
        var first = ProgramStatusStore.Empty.Apply(Report(ProgramStatusState.Done, "m"));
        Assert.Same(first, first.Apply(Report(ProgramStatusState.Done, "m")));
    }

    [Fact]
    public void Clear_without_an_id_removes_every_record()
    {
        var store = ProgramStatusStore.Empty.Apply(Report(ProgramStatusState.Done)).Apply(Report(ProgramStatusState.Clear));
        Assert.True(store.IsEmpty);
    }

    [Fact]
    public void Clear_on_an_empty_store_changes_no_record()
    {
        var store = ProgramStatusStore.Empty.Apply(Report(ProgramStatusState.Clear));
        Assert.True(store.IsEmpty);
        Assert.Equal(0, store.LastSeq);
        Assert.Same(store, store.Apply(Report(ProgramStatusState.Clear)));
    }

    [Fact]
    public void Sequence_keeps_growing_after_clear()
    {
        var store = ProgramStatusStore.Empty
            .Apply(Report(ProgramStatusState.Done))
            .Apply(Report(ProgramStatusState.Clear))
            .Apply(Report(ProgramStatusState.Idle));
        Assert.Equal(2, Assert.Single(store.Records).UpdatedSeq);
    }

    [Theory]
    [InlineData(ProgramStatusState.Working, 0)]
    [InlineData(ProgramStatusState.Blocked, 0)]
    [InlineData(ProgramStatusState.Done, 1)]
    [InlineData(ProgramStatusState.Error, 1)]
    [InlineData(ProgramStatusState.Idle, 1)]
    public void Process_exit_drops_working_and_blocked_only(ProgramStatusState state, int expectedCount)
    {
        var store = ProgramStatusStore.Empty.Apply(Report(state)).On(ProgramStatusLifecycleEvent.ProcessExit);
        Assert.Equal(expectedCount, store.Records.Length);
    }

    [Fact]
    public void Drop_running_with_nothing_to_drop_returns_the_same_store()
    {
        var store = ProgramStatusStore.Empty.Apply(Report(ProgramStatusState.Done));
        Assert.Same(store, store.On(ProgramStatusLifecycleEvent.ProcessExit));
    }

    [Fact]
    public void Reset_drops_every_record()
    {
        var store = ProgramStatusStore.Empty.Apply(Report(ProgramStatusState.Done)).On(ProgramStatusLifecycleEvent.FullReset);
        Assert.True(store.IsEmpty);
    }

    [Fact]
    public void One_program_example_gives_the_expected_records_in_order()
    {
        var store = ProgramStatusStore.Empty;
        var steps = new (ProgramStatusState State, ProgramStatusKind? Kind, string Msg)[]
        {
            (ProgramStatusState.Working, null, "Installing updates"),
            (ProgramStatusState.Blocked, ProgramStatusKind.Auth, "Password required to install updates"),
            (ProgramStatusState.Working, null, "Installing updates"),
            (ProgramStatusState.Done, null, "Upgraded 12 packages"),
        };
        long seq = 0;
        foreach (var (state, kind, msg) in steps)
        {
            store = store.Apply(Report(state, msg, "brew", kind: kind));
            var record = Assert.Single(store.Records);
            Assert.Equal(state, record.State);
            Assert.Equal(kind, record.Kind);
            Assert.Equal(msg, record.Msg);
            Assert.Equal("brew", record.App);
            Assert.Equal(++seq, record.UpdatedSeq);
        }

        // The done record survives the process exit that follows.
        Assert.Equal(ProgramStatusState.Done, Assert.Single(store.On(ProgramStatusLifecycleEvent.ProcessExit).Records).State);
    }

    [Theory]
    [InlineData(ProgramStatusLifecycleEvent.ProcessExit)]
    [InlineData(ProgramStatusLifecycleEvent.PromptStart)]
    public void Exit_and_prompt_drop_working_and_blocked_and_keep_the_rest(ProgramStatusLifecycleEvent e)
    {
        var store = ProgramStatusStore.Empty
            .Apply(Report(ProgramStatusState.Working, id: "a"))
            .Apply(Report(ProgramStatusState.Blocked, id: "b"))
            .Apply(Report(ProgramStatusState.Done, id: "c"))
            .Apply(Report(ProgramStatusState.Error, id: "d"))
            .Apply(Report(ProgramStatusState.Idle, id: "e"))
            .On(e);
        Assert.Equal("c,d,e", string.Join(",", store.Records.Select(r => r.Id)));
    }

    [Fact]
    public void Full_reset_drops_children_too() =>
        Assert.True(ProgramStatusStore.Empty.Apply(Report(ProgramStatusState.Done, id: "a/b")).On(ProgramStatusLifecycleEvent.FullReset).IsEmpty);

    // Records and ids

    [Fact]
    public void Several_records_example_gives_the_expected_records_after_each_report()
    {
        ProgramStatusStore Step(ProgramStatusStore s, ProgramStatusState st, string? id, string? msg, string? app = null, string? title = null, ProgramStatusKind? kind = null, int? progress = null) =>
            s.Apply(new ProgramStatusReport { State = st, Id = id, Msg = msg, App = app, Title = title, Kind = kind, Progress = progress });

        static string Ids(ProgramStatusStore s) => string.Join(",", s.Resolved().Select(r => r.Id ?? "<root>"));

        var s = Step(ProgramStatusStore.Empty, ProgramStatusState.Working, null, "Deploying v2.4.1", app: "deploy");
        Assert.Equal("<root>", Ids(s));
        s = Step(s, ProgramStatusState.Working, "us-east", "Pushing image", title: "US East", progress: 40);
        s = Step(s, ProgramStatusState.Blocked, "eu-west", "Approve deploy to eu-west (production)?", title: "EU West", kind: ProgramStatusKind.Permission);
        Assert.Equal("<root>,eu-west,us-east", Ids(s));
        // The region records take app=deploy from the root. The root still says working.
        var resolved = s.Resolved();
        Assert.All(resolved, r => Assert.Equal("deploy", r.App));
        Assert.Equal(ProgramStatusState.Working, resolved[0].State);
        Assert.Equal(ProgramStatusState.Blocked, resolved[1].State);
        s = Step(s, ProgramStatusState.Done, "us-east", "Healthy", title: "US East");
        Assert.Equal(ProgramStatusState.Done, s.Resolved()[2].State);
        Assert.Null(s.Resolved()[2].Progress);
        s = s.Apply(new ProgramStatusReport { State = ProgramStatusState.Clear, Id = "us-east" });
        Assert.Equal("<root>,eu-west", Ids(s));
        s = s.Apply(new ProgramStatusReport { State = ProgramStatusState.Clear, Id = "eu-west" });
        Assert.Equal("<root>", Ids(s));
        s = Step(s, ProgramStatusState.Done, null, "Deployed to 3 regions", app: "deploy");
        var root = Assert.Single(s.Resolved());
        Assert.Equal(ProgramStatusState.Done, root.State);
        Assert.Equal("Deployed to 3 regions", root.Msg);
    }

    [Fact]
    public void Clear_with_an_id_removes_the_subtree_and_keeps_similar_ids()
    {
        var store = ProgramStatusStore.Empty;
        foreach (var id in new[] { "build", "build/test", "build/test/unit", "buildx", "other" })
            store = store.Apply(Report(ProgramStatusState.Working, id: id));
        store = store.Apply(Report(ProgramStatusState.Clear, id: "build"));
        Assert.Equal("buildx,other", string.Join(",", store.Records.Select(r => r.Id)));
    }

    [Fact]
    public void Clear_with_an_id_that_matches_nothing_changes_nothing()
    {
        var store = ProgramStatusStore.Empty.Apply(Report(ProgramStatusState.Done, id: "a"));
        Assert.Same(store, store.Apply(Report(ProgramStatusState.Clear, id: "zzz")));
    }

    [Fact]
    public void Clear_with_an_id_keeps_the_root_record()
    {
        var store = ProgramStatusStore.Empty.Apply(Report(ProgramStatusState.Done)).Apply(Report(ProgramStatusState.Done, id: "a"));
        Assert.Null(Assert.Single(store.Apply(Report(ProgramStatusState.Clear, id: "a")).Records).Id);
    }

    [Fact]
    public void A_parent_does_not_have_to_exist()
    {
        var store = ProgramStatusStore.Empty.Apply(Report(ProgramStatusState.Done, id: "build/test"));
        Assert.Equal("build/test", Assert.Single(store.Records).Id);
    }

    [Fact]
    public void App_is_inherited_from_the_nearest_ancestor_that_has_one()
    {
        var store = ProgramStatusStore.Empty
            .Apply(Report(ProgramStatusState.Working, app: "root-app"))
            .Apply(Report(ProgramStatusState.Working, app: "build-app", id: "build"))
            .Apply(Report(ProgramStatusState.Working, id: "build/test"))
            .Apply(Report(ProgramStatusState.Working, app: "own", id: "build/lint"))
            .Apply(Report(ProgramStatusState.Working, id: "build/test/unit"))
            .Apply(Report(ProgramStatusState.Working, id: "solo"))
            .Apply(Report(ProgramStatusState.Working, id: "x/y/z"));
        var apps = store.Resolved().ToDictionary(r => r.Id ?? "<root>", r => r.App);
        Assert.Equal("build-app", apps["build"]);
        Assert.Equal("build-app", apps["build/test"]);
        Assert.Equal("own", apps["build/lint"]);
        Assert.Equal("build-app", apps["build/test/unit"]);
        Assert.Equal("root-app", apps["solo"]);
        Assert.Equal("root-app", apps["x/y/z"]);
        // The store keeps the record as reported.
        Assert.Null(store.Records.Single(r => r.Id == "build/test").App);
    }

    [Fact]
    public void Resolved_is_sorted_root_first_then_segment_by_segment()
    {
        var store = ProgramStatusStore.Empty;
        foreach (var id in new[] { "b", "a/b", "a-b", "a", "a/a" })
            store = store.Apply(Report(ProgramStatusState.Done, id: id));
        store = store.Apply(Report(ProgramStatusState.Done));
        Assert.Equal(",a,a/a,a/b,a-b,b", string.Join(",", store.Resolved().Select(r => r.Id)));
    }

    [Fact]
    public void The_257th_distinct_id_removes_the_least_recently_updated_record()
    {
        var store = ProgramStatusStore.Empty;
        for (var i = 0; i < ProgramStatusStore.MaxRecords; i++)
            store = store.Apply(Report(ProgramStatusState.Done, id: "r" + i));
        // Update r0, so r1 is now the oldest.
        store = store.Apply(Report(ProgramStatusState.Working, id: "r0"));
        store = store.Apply(Report(ProgramStatusState.Done, id: "extra"));
        Assert.Equal(ProgramStatusStore.MaxRecords, store.Records.Length);
        Assert.DoesNotContain(store.Records, r => r.Id == "r1");
        Assert.Contains(store.Records, r => r.Id == "r0");
        Assert.Contains(store.Records, r => r.Id == "extra");
    }

    [Fact]
    public void Updating_an_existing_id_at_the_cap_removes_nothing()
    {
        var store = ProgramStatusStore.Empty;
        for (var i = 0; i < ProgramStatusStore.MaxRecords; i++)
            store = store.Apply(Report(ProgramStatusState.Done, id: "r" + i));
        store = store.Apply(Report(ProgramStatusState.Working, id: "r5"));
        Assert.Equal(ProgramStatusStore.MaxRecords, store.Records.Length);
        Assert.Contains(store.Records, r => r.Id == "r0");
    }

    private static ProgramStatusStore Mapped(ProgramStatusStore store, ProgramStatusState state, int? progress = null) =>
        store.Apply(new ProgramStatusReport { State = state, Progress = progress }, ProgramStatusReportSource.ConEmuProgress);

    [Fact]
    public void Mapped_progress_makes_a_root_record_and_clear_removes_it()
    {
        var store = Mapped(ProgramStatusStore.Empty, ProgramStatusState.Working, 40);
        var record = Assert.Single(store.Records);
        Assert.Null(record.Id);
        Assert.Equal(40, record.Progress);
        Assert.False(store.RealReportSeen);
        Assert.True(Mapped(store, ProgramStatusState.Clear).IsEmpty);
    }

    [Fact]
    public void Mapped_error_and_indeterminate_replace_the_root_record()
    {
        var store = Mapped(ProgramStatusStore.Empty, ProgramStatusState.Working, 40);
        store = Mapped(store, ProgramStatusState.Error);
        Assert.Equal(ProgramStatusState.Error, Assert.Single(store.Records).State);
        store = Mapped(store, ProgramStatusState.Working);
        var record = Assert.Single(store.Records);
        Assert.Equal(ProgramStatusState.Working, record.State);
        Assert.Null(record.Progress);
    }

    [Fact]
    public void A_real_report_replaces_a_mapped_record_and_stops_the_mapping()
    {
        var store = Mapped(ProgramStatusStore.Empty, ProgramStatusState.Working, 40)
            .Apply(Report(ProgramStatusState.Blocked, "Need password", "brew", kind: ProgramStatusKind.Auth));
        Assert.True(store.RealReportSeen);
        var after = Mapped(store, ProgramStatusState.Working, 10);
        Assert.Same(store, after);
        var record = Assert.Single(after.Records);
        Assert.Equal(ProgramStatusState.Blocked, record.State);
        Assert.Equal(ProgramStatusKind.Auth, record.Kind);
        Assert.Equal("Need password", record.Msg);
        Assert.Same(store, Mapped(store, ProgramStatusState.Clear));
        Assert.Same(store, Mapped(store, ProgramStatusState.Error));
    }

    [Fact]
    public void A_real_clear_still_marks_the_store()
    {
        var store = ProgramStatusStore.Empty.Apply(Report(ProgramStatusState.Clear));
        Assert.True(store.RealReportSeen);
        Assert.True(Mapped(store, ProgramStatusState.Working, 10).IsEmpty);
        var withRecord = ProgramStatusStore.Empty.Apply(Report(ProgramStatusState.Done)).Apply(Report(ProgramStatusState.Clear));
        Assert.True(withRecord.RealReportSeen);
    }

    [Fact]
    public void A_real_report_with_an_id_marks_the_store_and_a_clear_by_id_keeps_the_mark()
    {
        var store = ProgramStatusStore.Empty.Apply(Report(ProgramStatusState.Working, id: "a"));
        Assert.True(store.RealReportSeen);
        Assert.True(store.Apply(Report(ProgramStatusState.Clear, id: "a")).RealReportSeen);
        Assert.True(store.Apply(Report(ProgramStatusState.Clear, id: "zz")).RealReportSeen);
    }

    [Fact]
    public void Full_reset_lets_mapped_progress_count_again()
    {
        var store = ProgramStatusStore.Empty.Apply(Report(ProgramStatusState.Done));
        Assert.True(Mapped(store, ProgramStatusState.Working, 10).RealReportSeen);
        var reset = store.On(ProgramStatusLifecycleEvent.FullReset);
        Assert.False(reset.RealReportSeen);
        Assert.Equal(10, Assert.Single(Mapped(reset, ProgramStatusState.Working, 10).Records).Progress);

        var emptyAfterClear = ProgramStatusStore.Empty.Apply(Report(ProgramStatusState.Clear));
        Assert.False(emptyAfterClear.On(ProgramStatusLifecycleEvent.FullReset).RealReportSeen);
    }

    [Fact]
    public void Process_exit_and_prompt_start_keep_the_mark_and_drop_mapped_working()
    {
        var mapped = Mapped(ProgramStatusStore.Empty, ProgramStatusState.Working, 40);
        Assert.True(mapped.On(ProgramStatusLifecycleEvent.PromptStart).IsEmpty);
        Assert.True(mapped.On(ProgramStatusLifecycleEvent.ProcessExit).IsEmpty);
        var real = ProgramStatusStore.Empty.Apply(Report(ProgramStatusState.Done));
        Assert.True(real.On(ProgramStatusLifecycleEvent.PromptStart).RealReportSeen);
        Assert.True(real.On(ProgramStatusLifecycleEvent.ProcessExit).RealReportSeen);
    }

    [Fact]
    public void Mapped_clear_leaves_records_with_an_id()
    {
        // Only a store that was built by hand can hold both. The rule is still root only.
        var store = Mapped(ProgramStatusStore.Empty, ProgramStatusState.Working, 5);
        Assert.Equal(store.Records.Length, Mapped(store, ProgramStatusState.Working, 5).Records.Length);
        Assert.Same(ProgramStatusStore.Empty, Mapped(ProgramStatusStore.Empty, ProgramStatusState.Clear));
    }
}
