// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections;
using Robust.Shared.GameObjects;

namespace Content.Server._Harvest.AndroidNpc.BehaviorTree.Nodes;

public enum BehaviorStatus
{
    Success,
    Failure,
    Running
}

/// <summary>
/// A behavior tree node, evaluated once per tick. Persistent state belongs in the android's components.
/// </summary>
public interface IBehaviorNode
{
    BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime);
}

/// <summary>
/// Succeeds when the predicate holds.
/// </summary>
public sealed class ConditionNode(Func<EntityUid, AndroidBlackboard, bool> condition) : IBehaviorNode
{
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        return condition(owner, blackboard) ? BehaviorStatus.Success : BehaviorStatus.Failure;
    }
}

/// <summary>
/// Runs a side effect and succeeds.
/// </summary>
public sealed class DoNode(Action<EntityUid, AndroidBlackboard> action) : IBehaviorNode
{
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        action(owner, blackboard);
        return BehaviorStatus.Success;
    }
}

/// <summary>
/// Returns the status computed by a function, for one-off actions owned by a system.
/// </summary>
public sealed class TickNode(Func<EntityUid, AndroidBlackboard, BehaviorStatus> tick) : IBehaviorNode
{
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime) => tick(owner, blackboard);
}

/// <summary>
/// A node with ordered children. Named composites are recorded in the blackboard trace for debugging.
/// </summary>
public abstract class CompositeNode(string? name) : IBehaviorNode, IEnumerable<IBehaviorNode>
{
    private readonly List<IBehaviorNode> _children = new();

    public string? Name { get; } = name;

    public IReadOnlyList<IBehaviorNode> Children => _children;

    public void Add(IBehaviorNode child) => _children.Add(child);

    /// <summary>
    /// The child status that lets evaluation continue with the next child.
    /// </summary>
    protected abstract BehaviorStatus ContinueOn { get; }

    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        var trace = Name != null ? blackboard.Trace : null;
        var index = trace?.Count ?? 0;
        trace?.Add(string.Empty);
        blackboard.TraceDepth++;

        var result = ContinueOn;
        foreach (var child in _children)
        {
            var status = child.Tick(owner, blackboard, frameTime);
            if (status == ContinueOn)
                continue;

            result = status;
            break;
        }

        blackboard.TraceDepth--;
        if (trace != null)
            trace[index] = $"{new string(' ', blackboard.TraceDepth * 2)}{Name}: {result}";

        return result;
    }

    public IEnumerator<IBehaviorNode> GetEnumerator() => _children.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Runs children in order until one succeeds or is running.
/// </summary>
public sealed class SelectorNode(string? name = null) : CompositeNode(name)
{
    protected override BehaviorStatus ContinueOn => BehaviorStatus.Failure;
}

/// <summary>
/// Runs children in order until one fails or is running.
/// </summary>
public sealed class SequenceNode(string? name = null) : CompositeNode(name)
{
    protected override BehaviorStatus ContinueOn => BehaviorStatus.Success;
}
