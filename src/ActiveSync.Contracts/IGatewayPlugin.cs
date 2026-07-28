// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ActiveSync.Contracts;

/// <summary>
///   Entry point of an out-of-repo plugin assembly dropped into the plugins directory. The
///   loader instantiates each public implementation (parameterless ctor) and calls
///   <see cref="Register" /> during service registration, so a plugin registers its backend
///   providers exactly like the in-repo ones — <c>services.AddSingleton&lt;IBackendProvider,
///   MyProvider&gt;()</c> — plus anything they depend on. The provider then serves any role
///   that config assigns to its <c>Name</c>.
/// </summary>
public interface IGatewayPlugin
{
	/// <summary>
	///   Registers this plugin's backend providers (and anything they depend on) into the host's
	///   service collection — typically one or more
	///   <c>services.AddSingleton&lt;IBackendProvider, MyProvider&gt;()</c> calls. Called once
	///   during host service registration, before the service provider is built.
	/// </summary>
	/// <param name="services">The host's service collection to register into.</param>
	/// <param name="configuration">The host's root configuration, for reading plugin-specific settings if needed.</param>
	void Register(IServiceCollection services, IConfiguration configuration);
}
