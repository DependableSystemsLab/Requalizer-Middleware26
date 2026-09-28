using System;
using System.Collections.Generic;

namespace OneOS.Common;

/// <summary>
/// Event arguments for when a transition has started.
/// </summary>
public class TransitionEventArgs<TState, TTrigger> : EventArgs
    where TState : struct, Enum
    where TTrigger : struct, Enum
{
    public TState FromState { get; }
    public TState ToState { get; }
    public TTrigger Trigger { get; }

    public TransitionEventArgs(TState from, TTrigger trigger, TState to)
    {
        FromState = from;
        Trigger = trigger;
        ToState = to;
    }
}

/// <summary>
/// Event arguments for when a transition has completed successfully and state has changed.
/// </summary>
public class StateChangedEventArgs<TState, TTrigger> : EventArgs
    where TState : struct, Enum
    where TTrigger : struct, Enum
{
    public TState PreviousState { get; }
    public TState CurrentState { get; }
    public TTrigger Trigger { get; }

    public StateChangedEventArgs(TState previous, TTrigger trigger, TState current)
    {
        PreviousState = previous;
        Trigger = trigger;
        CurrentState = current;
    }
}

/// <summary>
/// A generic, thread-safe finite state machine.
/// Designed to strictly enforce valid phase transitions and decouple state from heavy execution logic.
/// </summary>
public class StateMachine<TState, TTrigger>
    where TState : struct, Enum
    where TTrigger : struct, Enum
{
    private readonly object _syncRoot = new();

    // Maps a (CurrentState, Trigger) directly to a target State
    private readonly Dictionary<(TState, TTrigger), TState> _transitions = new();

    /// <summary>
    /// The current state of the machine.
    /// </summary>
    public TState CurrentState { get; private set; }

    public event EventHandler<TransitionEventArgs<TState, TTrigger>>? TransitionStarted;
    public event EventHandler<StateChangedEventArgs<TState, TTrigger>>? StateChanged;

    public StateMachine(TState initialState)
    {
        CurrentState = initialState;
    }

    /// <summary>
    /// Forcibly sets the state machine to a specific state, bypassing transition rules.
    /// Use sparingly, usually only for initialization or catastrophic resets.
    /// </summary>
    public void ForceSetState(TState state)
    {
        lock (_syncRoot)
        {
            CurrentState = state;
        }
    }

    /// <summary>
    /// Adds a valid transition from one state to another, triggered by a specific event.
    /// </summary>
    public void AddTransition(TState from, TTrigger trigger, TState to)
    {
        var key = (from, trigger);
        lock (_syncRoot)
        {
            if (_transitions.ContainsKey(key))
            {
                throw new InvalidOperationException($"A transition from '{from}' with trigger '{trigger}' is already defined.");
            }

            _transitions[key] = to;
        }
    }

    /// <summary>
    /// Evaluates whether a specific trigger is valid for the current state.
    /// </summary>
    public bool CanFire(TTrigger trigger)
    {
        lock (_syncRoot)
        {
            return _transitions.ContainsKey((CurrentState, trigger));
        }
    }

    /// <summary>
    /// Attempts to fire the given trigger to progress the state machine.
    /// </summary>
    /// <param name="trigger">The trigger to fire.</param>
    /// <returns>True if the transition completed successfully; false if no transition exists for the current state.</returns>
    public bool Fire(TTrigger trigger)
    {
        TState previousState;
        TState targetState;

        lock (_syncRoot)
        {
            var key = (CurrentState, trigger);
            if (!_transitions.TryGetValue(key, out targetState))
            {
                // Invalid trigger for current state
                return false; 
            }

            previousState = CurrentState;
            CurrentState = targetState;
        }

        // Fire events *outside* the lock to prevent deadlocks 
        // if event handlers attempt to interact with the FSM.
        TransitionStarted?.Invoke(this, new TransitionEventArgs<TState, TTrigger>(previousState, trigger, targetState));
        StateChanged?.Invoke(this, new StateChangedEventArgs<TState, TTrigger>(previousState, trigger, targetState));

        return true;
    }

    /// <summary>
    /// Attempts to fire the given trigger. Throws if the transition is invalid.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the trigger is invalid for the current state.</exception>
    public void FireStrict(TTrigger trigger)
    {
        if (!Fire(trigger))
        {
            throw new InvalidOperationException($"Cannot fire trigger '{trigger}' from state '{CurrentState}'.");
        }
    }
}
