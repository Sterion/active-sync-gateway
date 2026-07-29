// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

namespace ActiveSync.Contracts.Conformance;

/// <summary>How one conformance check ended.</summary>
public enum ConformanceOutcome
{
	/// <summary>The store met the obligation.</summary>
	Passed,

	/// <summary>The store broke the obligation. The check's detail says how.</summary>
	Failed,

	/// <summary>
	///   The check does not apply to this store and nothing was proved — an optional capability it
	///   does not implement, or a class whose writes the kit deliberately does not synthesize.
	///   Skipped is NOT a failure: the contract has genuine "a store that cannot do this is still
	///   conforming" clauses, and reporting them as passes would be a lie.
	/// </summary>
	Skipped
}

/// <summary>One store obligation, checked.</summary>
public sealed record ConformanceCheck
{
	/// <summary>Stable dotted identifier, e.g. <c>revisions.stable</c>. Safe to match on.</summary>
	public required string Name { get; init; }

	/// <summary>How the check ended.</summary>
	public required ConformanceOutcome Outcome { get; init; }

	/// <summary>
	///   What happened, in one line — the observed values for a failure, the reason for a skip.
	/// </summary>
	public required string Detail { get; init; }

	/// <summary>Renders the check as "outcome name: detail", for a test's assertion message.</summary>
	/// <returns>A single-line description.</returns>
	public override string ToString() => $"{Outcome} {Name}: {Detail}";
}

/// <summary>The result of running the kit against one store.</summary>
public sealed record ConformanceReport
{
	/// <summary>Every check the run performed, in the order it ran them.</summary>
	public required IReadOnlyList<ConformanceCheck> Checks { get; init; }

	/// <summary>Whether no check failed. Skipped checks do not fail a run.</summary>
	public bool Passed => !Checks.Any(check => check.Outcome == ConformanceOutcome.Failed);

	/// <summary>The failures alone — what a test's assertion message should print.</summary>
	public IReadOnlyList<ConformanceCheck> Failures =>
		[.. Checks.Where(check => check.Outcome == ConformanceOutcome.Failed)];

	/// <summary>Renders every check, one per line.</summary>
	/// <returns>The full report as text.</returns>
	public override string ToString() => string.Join(Environment.NewLine, Checks);
}

/// <summary>What the run is allowed to do, and where.</summary>
public sealed record ConformanceOptions
{
	/// <summary>
	///   The folder to exercise. Null picks the first folder the store lists — right for a store
	///   with one fixed collection, worth setting explicitly for anything else.
	/// </summary>
	public FolderKey? Folder { get; init; }

	/// <summary>
	///   Whether the run may CREATE, UPDATE and DELETE an item in that folder (it cleans up after
	///   itself, but it does write). False restricts the run to read-only checks — the right
	///   setting against a live account you care about.
	/// </summary>
	public bool AllowMutation { get; init; } = true;

	/// <summary>
	///   How long <see cref="IContentStore.WaitForChangesAsync" /> is given in the timeout check.
	///   Kept short by default: the check proves the timeout is honoured, not how push works.
	/// </summary>
	public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(2);
}
