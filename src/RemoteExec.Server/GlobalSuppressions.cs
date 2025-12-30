// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "<Pending>", Scope = "type", Target = "~T:RemoteExec.Server.Hubs.RemoteExecutionHub")]
[assembly: SuppressMessage("Major Code Smell", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields", Justification = "<Pending>", Scope = "member", Target = "~M:RemoteExec.Server.Hubs.RemoteExecutionHub.Execute(RemoteExec.Shared.RemoteExecutionRequest)~System.Threading.Tasks.Task{RemoteExec.Shared.RemoteExecutionResult}")]
[assembly: SuppressMessage("Major Code Smell", "S3010:Static fields should not be updated in constructors", Justification = "<Pending>")]
[assembly: SuppressMessage("Performance", "CA1873:Avoid potentially expensive logging", Justification = "Annoying")]
[assembly: SuppressMessage("Major Code Smell", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields", Justification = "<Pending>", Scope = "member", Target = "~M:RemoteExec.Server.Services.AssemblyLoadContextExecutionEnvironment.ExecuteTaskAsync(RemoteExec.Shared.Models.RemoteExecutionRequest)~System.Threading.Tasks.Task{RemoteExec.Shared.Models.RemoteExecutionResult}")]
