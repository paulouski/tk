using Tk.Commands;
using Xunit;

namespace Tk.Tests.Commands;

public class BuiltinRegistryTests
{
    private sealed class FakeCommand(string name) : ICommand
    {
        public string Name { get; } = name;
        public Task<int> RunAsync(CommandContext ctx) => Task.FromResult(0);
    }

    [Fact]
    public void Resolves_registered_command_by_name()
    {
        var foo = new FakeCommand("foo");
        var registry = new BuiltinRegistry([foo, new FakeCommand("bar")]);

        Assert.True(registry.TryResolve("foo", out var resolved));
        Assert.Same(foo, resolved);
    }

    [Fact]
    public void Returns_false_for_unknown_command()
    {
        var registry = new BuiltinRegistry([new FakeCommand("foo")]);
        Assert.False(registry.TryResolve("nope", out _));
    }

    [Fact]
    public void Empty_registry_resolves_nothing()
    {
        var registry = new BuiltinRegistry([]);
        Assert.False(registry.TryResolve("foo", out _));
        Assert.Empty(registry.Names);
    }

    [Fact]
    public void Name_lookup_is_case_sensitive()
    {
        var registry = new BuiltinRegistry([new FakeCommand("foo")]);
        Assert.False(registry.TryResolve("Foo", out _));
        Assert.False(registry.TryResolve("FOO", out _));
    }

    [Fact]
    public void Duplicate_names_throw_on_construction()
    {
        Assert.Throws<ArgumentException>(() =>
            new BuiltinRegistry([new FakeCommand("foo"), new FakeCommand("foo")]));
    }
}
