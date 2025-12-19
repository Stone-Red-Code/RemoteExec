// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "<Pending>", Scope = "type", Target = "~T:RemoteExec.Server.Hubs.RemoteExecutionHub")]
[assembly: SuppressMessage("Major Code Smell", "S2139:Exceptions should be either logged or rethrown but not both", Justification = "<Pending>", Scope = "member", Target = "~M:RemoteExec.Server.Hubs.RemoteExecutionHub.RequestAssemblyAsync(System.String)~System.Threading.Tasks.Task{System.Reflection.Assembly}")]
[assembly: SuppressMessage("Major Code Smell", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields", Justification = "<Pending>", Scope = "member", Target = "~M:RemoteExec.Server.Hubs.RemoteExecutionHub.Execute(RemoteExec.Shared.RemoteExecutionRequest)~System.Threading.Tasks.Task{RemoteExec.Shared.RemoteExecutionResult}")]
