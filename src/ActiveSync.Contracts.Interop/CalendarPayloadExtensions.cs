// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

using Ical.Net;

namespace ActiveSync.Contracts.Interop;

/// <summary>
///   Converts between the iCalendar payload records (<see cref="CalendarItem" />,
///   <see cref="TaskItem" />) and Ical.Net's object model.
/// </summary>
/// <remarks>
///   The mapping is to <see cref="Calendar" />, the container — never to a bare
///   <c>CalendarEvent</c> / <c>Todo</c>. A calendar item's VTIMEZONE components and the properties
///   around the event are part of what the store must round-trip, and taking the component alone
///   would silently drop them.
/// </remarks>
public static class CalendarPayloadExtensions
{
	/// <summary>Parses an event payload into Ical.Net's object model.</summary>
	/// <param name="item">The event as it crossed the store boundary.</param>
	/// <returns>The parsed calendar, with the event among its <see cref="Calendar.Events" />.</returns>
	/// <exception cref="BackendException">The payload is not parsable iCalendar.</exception>
	public static Calendar ToCalendar(this CalendarItem item)
	{
		ArgumentNullException.ThrowIfNull(item);
		return IcalHelpers.Load(item.ICalendar);
	}

	/// <summary>Parses a task payload into Ical.Net's object model.</summary>
	/// <param name="item">The task as it crossed the store boundary.</param>
	/// <returns>The parsed calendar, with the task among its <see cref="Calendar.Todos" />.</returns>
	/// <exception cref="BackendException">The payload is not parsable iCalendar.</exception>
	public static Calendar ToCalendar(this TaskItem item)
	{
		ArgumentNullException.ThrowIfNull(item);
		return IcalHelpers.Load(item.ICalendar);
	}

	/// <summary>Serializes a calendar into the event payload the contract expects.</summary>
	/// <param name="calendar">The calendar holding the VEVENT (with any VTIMEZONE it needs).</param>
	/// <returns>The event as the contract carries it.</returns>
	public static CalendarItem ToCalendarItem(this Calendar calendar)
	{
		ArgumentNullException.ThrowIfNull(calendar);
		return new CalendarItem { ICalendar = IcalHelpers.Serialize(calendar) };
	}

	/// <summary>Serializes a calendar into the task payload the contract expects.</summary>
	/// <param name="calendar">The calendar holding the VTODO.</param>
	/// <returns>The task as the contract carries it.</returns>
	public static TaskItem ToTaskItem(this Calendar calendar)
	{
		ArgumentNullException.ThrowIfNull(calendar);
		return new TaskItem { ICalendar = IcalHelpers.Serialize(calendar) };
	}
}
