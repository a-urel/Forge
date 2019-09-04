//-----------------------------------------------------------------------
// <copyright file="TreeWalkerSession.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
// <summary>
//     The TreeWalkerSession class.
// </summary>
//-----------------------------------------------------------------------

namespace Forge.TreeWalker
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Reflection;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis.Scripting;

    using Forge.Attributes;
    using Forge.DataContracts;
    using Forge.TreeWalker.ForgeExceptions;

    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// The TreeWalkerSession tries to walk the given tree schema to completion.
    /// It holds the logic for walking the tree schema, executing actions, and calling callbacks.
    /// </summary>
    public class TreeWalkerSession : ITreeSession
    {
        /// <summary>
        /// The ActionResponse suffix appended to the end of the key in forgeState that maps to an ActionResponse.
        /// Key: <SessionId>_<TreeActionKey>_AR
        /// </summary>
        public static string ActionResponseSuffix = "_AR";

        /// <summary>
        /// The CurrentTreeNode suffix appended to the end of the key in forgeState that maps to the current TreeNode being walked.
        /// Key: <SessionId>_CTN
        /// </summary>
        public static string CurrentTreeNodeSuffix = "CTN";

        /// <summary>
        /// The LastTreeAction suffix appended to the end of the key in forgeState that maps to the last TreeAction that was committed.
        /// Key: <SessionId>_LTA
        /// </summary>
        public static string LastTreeActionSuffix = "LTA";

        /// <summary>
        /// The Intermediates suffix appended to the end of the key in forgeState that maps to an ActionContext's GetIntermediates object.
        /// Key: <SessionId>_<TreeActionKey>_Int
        /// </summary>
        public static string IntermediatesSuffix = "_Int";

        /// <summary>
        /// The name of the native LeafNodeSummaryAction.
        /// This Action can only live on Leaf type TreeNodes. Similarly, Leaf type TreeNodes can only have this Action.
        /// This Action takes an ActionResponse as the ActionInput, either as an object or as properties, and commits this object as the ActionResponse.
        /// This Action is intended to give schema authors the ability to cleanly end a tree walking path with a summary.
        /// </summary>
        public static string LeafNodeSummaryAction = "LeafNodeSummaryAction";

        /// <summary>
        /// The Roslyn regex expression. Used to check if dynamic schema values should be evaluated with Roslyn.
        /// Type can be added to indicate that Roslyn should evaluate the expression and return the specified type.
        /// If the property is a "KnownType", the KnownType is used even if a <type> is specified in the expression. ActionDefinition.InputType is an example of a KnownType.
        /// If the property is not a KnownType and no type is specified in the expression, string is used by default.
        ///     Ex) C#|"expression"
        ///     Ex) C#<Boolean>|"expression"
        /// </summary>
        private static Regex RoslynRegex = new Regex(@"^C#(\<(\w+)\>)?\|");

        /// <summary>
        /// The leading text to add to Schema strings to indicate the property value should be evaluated with Roslyn.
        /// The property value must match the RoslynRegex to be evaluated with Roslyn.
        /// </summary>
        private static string RoslynLeadingText = "C#";

        /// <summary>
        /// The TreeWalkerParameters contains the required and optional properties used by the TreeWalkerSession.
        /// </summary>
        public TreeWalkerParameters Parameters { get; private set; }

        /// <summary>
        /// The JSON schema holding the tree to navigate during WalkTree.
        /// </summary>
        public ForgeTree Schema { get; private set; }

        /// <summary>
        /// The current status of the tree walker.
        /// </summary>
        public string Status { get; private set; }

        /// <summary>
        /// The WalkTree cancellation token source.
        /// Used to send cancellation signal to action tasks and to stop tree walker from visiting future nodes.
        /// </summary>
        private CancellationTokenSource walkTreeCts;

        /// <summary>
        /// The ExpressionExecutor dynamically compiles code and executes it.
        /// </summary>
        private ExpressionExecutor expressionExecutor;

        /// <summary>
        /// The map of string ActionNames to ActionDefinitions.
        /// This map is generated using reflection to find all the classes with the applied ForgeActionAttribute from the given Assembly.
        /// The string key is the Action class name.
        /// The ActionDefinition value contains the Action class type, and the InputType for the Action.
        /// </summary>
        private Dictionary<string, ActionDefinition> actionsMap;

        /// <summary>
        /// Instantiates a tree walker session with the required parameters.
        /// </summary>
        /// <param name="parameters">The parameters object contains the required and optional properties used by the TreeWalkerSession.</param>
        public TreeWalkerSession(TreeWalkerParameters parameters)
        {
            if (parameters == null) throw new ArgumentNullException("parameters");

            this.Parameters = parameters;

            // Initialize properties from required TreeWalkerParameters properties.
            this.Schema = JsonConvert.DeserializeObject<ForgeTree>(parameters.JsonSchema);
            this.walkTreeCts = CancellationTokenSource.CreateLinkedTokenSource(parameters.Token);

            // Initialize properties from optional TreeWalkerParameters properties.
            GetActionsMapFromAssembly(parameters.ForgeActionsAssembly, out this.actionsMap);
            parameters.ExternalExecutors = parameters.ExternalExecutors ?? new Dictionary<string, Func<string, CancellationToken, Task<object>>>();
            this.expressionExecutor = new ExpressionExecutor(this as ITreeSession, parameters.UserContext, parameters.Dependencies, parameters.ScriptCache);

            this.Status = "Initialized";
        }

        /// <summary>
        /// Gets the ActionResponse data from the forgeState for the given tree action key.
        /// </summary>
        /// <param name="treeActionKey">The TreeAction's key of the action that was executed.</param>
        /// <returns>The ActionResponse data for the given tree action key if it exists, otherwise null.</returns>
        public ActionResponse GetOutput(string treeActionKey)
        {
            try
            {
                return this.Parameters.ForgeState.GetValue<ActionResponse>(treeActionKey + ActionResponseSuffix).GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Asynchronously gets the ActionResponse data from the forgeState for the given tree action key.
        /// </summary>
        /// <param name="treeActionKey">The TreeAction's key of the action that was executed.</param>
        /// <returns>The ActionResponse data for the given tree action key if it exists, otherwise null.</returns>
        public async Task<ActionResponse> GetOutputAsync(string treeActionKey)
        {
            try
            {
                return await this.Parameters.ForgeState.GetValue<ActionResponse>(treeActionKey + ActionResponseSuffix).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets the last executed TreeAction's ActionResponse data from the forgeState.
        /// </summary>
        /// <returns>The ActionResponse data for the last executed tree action key if it exists, otherwise null.</returns>
        public ActionResponse GetLastActionResponse()
        {
            try
            {
                return this.Parameters.ForgeState.GetValue<ActionResponse>(this.GetLastTreeAction().GetAwaiter().GetResult() + ActionResponseSuffix).GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Asynchronously gets the last executed TreeAction's ActionResponse data from the forgeState.
        /// </summary>
        /// <returns>The ActionResponse data for the last executed tree action key if it exists, otherwise null.</returns>
        public async Task<ActionResponse> GetLastActionResponseAsync()
        {
            try
            {
                return await this.Parameters.ForgeState.GetValue<ActionResponse>(await this.GetLastTreeAction().ConfigureAwait(false) + ActionResponseSuffix).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets the current tree node being walked from the forgeState.
        /// </summary>
        /// <returns>The current tree node if it has been persisted, otherwise null.</returns>
        public async Task<string> GetCurrentTreeNode()
        {
            try
            {
                return await this.Parameters.ForgeState.GetValue<string>(CurrentTreeNodeSuffix).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets the last committed tree action from the forgeState.
        /// </summary>
        /// <returns>The last committed tree action if it has been persisted, otherwise null.</returns>
        public async Task<string> GetLastTreeAction()
        {
            try
            {
                return await this.Parameters.ForgeState.GetValue<string>(LastTreeActionSuffix).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Signals the WalkTree and VisitNode cancellation token sources to cancel.
        /// </summary>
        public void CancelWalkTree()
        {
            this.walkTreeCts.Cancel();
        }

        /// <summary>
        /// Walks the tree schema starting at the given tree node key.
        /// </summary>
        /// <param name="treeNodeKey">The TreeNode key to start walking.</param>
        /// <returns>The string status of the tree walker.</returns>
        public async Task<string> WalkTree(string treeNodeKey)
        {
            this.Status = "Running";

            // Start a single task to walk the tree to completion.
            // This task will only start new tasks to execute actions.
            Task walkTreeTask = Task.Run(async () =>
            {
                string current = treeNodeKey;
                string next;

                // Starting with the given tree node key, visit the returned children nodes until you hit a node with no matching children.
                // Call the callbacks before/after visiting each node.
                do
                {
                    await this.CommitCurrentTreeNode(current).ConfigureAwait(false);
                    if (this.walkTreeCts.Token.IsCancellationRequested)
                    {
                        this.Status = "Cancelled";
                        this.walkTreeCts.Token.ThrowIfCancellationRequested();
                    }

                    await this.Parameters.Callbacks.BeforeVisitNode(
                        this.Parameters.SessionId,
                        current,
                        await this.EvaluateDynamicProperty(this.Schema.Tree[current].Properties, null),
                        this.Parameters.UserContext,
                        this.walkTreeCts.Token).ConfigureAwait(false);

                    try
                    {
                        // Exceptions are thrown here if VisitNode hit a timeout, was cancelled, or failed.
                        next = await this.VisitNode(current).ConfigureAwait(false);
                    }
                    finally
                    {
                        // Always call this callback, whether or not visit node was successful.
                        await this.Parameters.Callbacks.AfterVisitNode(
                            this.Parameters.SessionId,
                            current,
                            await this.EvaluateDynamicProperty(this.Schema.Tree[current].Properties, null),
                            this.Parameters.UserContext,
                            this.walkTreeCts.Token).ConfigureAwait(false);
                    }

                    current = next;
                } while (!string.IsNullOrWhiteSpace(current));

                if (string.IsNullOrWhiteSpace(current))
                {
                    // Null child means the tree ran to completion.
                    this.Status = "RanToCompletion";
                }
            }, this.walkTreeCts.Token);

            try
            {
                // Exceptions are thrown here if tree walker hit a timeout, was cancelled, or failed.
                // Let's update the Status according to the exception thrown before rethrowing the exception.
                await walkTreeTask;
            }
            catch (TaskCanceledException)
            {
                // Tree walker was cancelled before calling WalkTree.
                this.Status = "CancelledBeforeExecution";
            }
            catch (OperationCanceledException)
            {
                // Tree walker was cancelled after calling WalkTree (no timeouts hit).
                this.Status = "Cancelled";
            }
            catch (ActionTimeoutException)
            {
                // An action-level timeout was hit.
                this.Status = "TimeoutOnAction";
            }
            catch (TimeoutException)
            {
                // A node-level timeout was hit.
                this.Status = "TimeoutOnNode";
            }
            catch (NoChildMatchedException)
            {
                // ChildSelector couldn't select any child.
                this.Status = "RanToCompletion_NoChildMatched";
            }
            catch (EvaluateDynamicPropertyException)
            {
                this.Status = "Failed_EvaluateDynamicProperty";
            }
            catch (Exception)
            {
                // TODO: Consider checking the exception for specific Data entry and setting Status to that.
                //       This would allow callbacks such as BeforeVisitNode to throw exceptions and control the status instead of it being Failed.
                // Unexpected exception was thrown in actions, callbacks, or elsewhere, resulting in failure and cancellation.
                this.Status = "Failed";
            }
            finally
            {
                this.CancelWalkTree();
            }

            try
            {
                // Exceptions are thrown here if tree walker hit a timeout, was cancelled, or failed.
                await walkTreeTask;
            }
            catch (NoChildMatchedException)
            {
                // For now, suppressing this exception so that its treated as successful end stage.
            }

            return this.Status;
        }

        /// <summary>
        /// Visits a TreeNode in the ForgeTree, performing type-specific behavior as necessary before selecting the next child to visit.
        /// </summary>
        /// <param name="treeNodeKey">The TreeNode key to visit.</param>
        /// <exception cref="TimeoutException">If the node-level timeout was hit.</exception>
        /// <exception cref="ActionTimeoutException">If the action-level timeout was hit.</exception>
        /// <exception cref="OperationCanceledException">If the cancellation token was triggered.</exception>
        /// <exception cref="Exception">If an unexpected exception was thrown.</exception>
        /// <returns>The key of the next child to visit, or <c>null</c> if no match was found.</returns>
        internal async Task<string> VisitNode(string treeNodeKey)
        {
            TreeNode treeNode = this.Schema.Tree[treeNodeKey];

            // Do type-specific behavior.
            switch (treeNode.Type)
            {
                case TreeNodeType.Leaf:
                {
                    await this.PerformLeafTypeBehavior(treeNode).ConfigureAwait(false);

                    // Leaf type can't have ChildSelector so we return here.
                    return null;
                }
                case TreeNodeType.Action:
                {
                    // Exceptions are thrown here if the actions hit a timeout, were cancelled, or failed.
                    await this.PerformActionTypeBehavior(treeNode, treeNodeKey).ConfigureAwait(false);
                    break;
                }
                default:
                {
                    break;
                }
            }

            // Return next child to visit, if possible.
            return await this.SelectChild(treeNode).ConfigureAwait(false);
        }

        /// <summary>
        /// Iterates the child selectors for a matching child.
        /// </summary>
        /// <param name="treeNode">The TreeNode to select a child from.</param>
        /// <returns>The key of the next child to visit, or <c>null</c> if no match was found.</returns>
        internal async Task<string> SelectChild(TreeNode treeNode)
        {
            if (treeNode.ChildSelector == null)
            {
                // No children to select, we are done walking the tree.
                return null;
            }

            foreach (ChildSelector cs in treeNode.ChildSelector)
            {
                // Empty expressions default to true. Otherwise, evaluate the expression.
                if (string.IsNullOrWhiteSpace(cs.ShouldSelect) && !string.IsNullOrWhiteSpace(cs.Child))
                {
                    return cs.Child;
                }
                if ((bool)await this.EvaluateDynamicProperty(cs.ShouldSelect, typeof(bool)).ConfigureAwait(false))
                {
                    return cs.Child;
                }
            }

            // No children were successfully matched.
            throw new NoChildMatchedException("ChildSelector couldn't match any child.");
        }

        /// <summary>
        /// Performs Leaf TreeNodeType behavior.
        /// </summary>
        /// <param name="treeNode">The Leaf TreeNode.</param>
        internal async Task PerformLeafTypeBehavior(TreeNode treeNode)
        {
            // Check if Leaf node contains the LeafNodeSummaryAction and commit ActionInput as ActionResponse if it does.
            if (treeNode.Actions == null || treeNode.Actions.Count != 1)
            {
                return;
            }

            foreach (KeyValuePair<string, TreeAction> kvp in treeNode.Actions)
            {
                string treeActionKey = kvp.Key;
                TreeAction treeAction = kvp.Value;

                if (treeAction.Action != LeafNodeSummaryAction)
                {
                    return;
                }

                ActionResponse actionResponse = await this.EvaluateDynamicProperty(treeAction.Input, typeof(ActionResponse)).ConfigureAwait(false);
                await this.CommitActionResponse(treeActionKey, actionResponse).ConfigureAwait(false);
                return;
            }
        }

        /// <summary>
        /// Executes the actions for the given tree node.
        /// Returns without throwing exception if all actions were completed successfully.
        /// </summary>
        /// <param name="treeNode">The TreeNode containing actions to execute.</param>
        /// <param name="treeNodeKey">The TreeNode's key where the actions are taking place.</param>
        /// <exception cref="TimeoutException">If the node-level timeout was hit.</exception>
        /// <exception cref="ActionTimeoutException">If the action-level timeout was hit.</exception>
        /// <exception cref="OperationCanceledException">If the cancellation token was triggered.</exception>
        internal async Task PerformActionTypeBehavior(TreeNode treeNode, string treeNodeKey)
        {
            List<Task> actionTasks = new List<Task>();

            if (treeNode.Actions == null)
            {
                return;
            }

            // Start new parallel tasks for each action on this node.
            foreach (KeyValuePair<string, TreeAction> kvp in treeNode.Actions)
            {
                string treeActionKey = kvp.Key;
                TreeAction treeAction = kvp.Value;

                if (await this.GetOutputAsync(treeActionKey).ConfigureAwait(false) != null)
                {
                    // Handle rehydration case. Commit LastTreeAction if it was not committed. Do not execute actions for which we have already received a response.
                    if (await this.GetLastTreeAction().ConfigureAwait(false) == null)
                    {
                        await this.CommitLastTreeAction(treeActionKey).ConfigureAwait(false);
                    }

                    continue;
                }

                if (this.actionsMap.TryGetValue(treeAction.Action, out ActionDefinition actionDefinition))
                {
                    actionTasks.Add(this.ExecuteActionWithRetry(treeNodeKey, treeActionKey, treeAction, actionDefinition, this.walkTreeCts.Token));
                }
            }

            // Wait for all parallel tasks to complete until the given timout.
            // If any task hits a timeout, gets cancelled, or fails, an exception will be thrown.
            // Note: CancelWalkTree is called at the end of every session to ensure all Actions/Tasks see the triggered cancellation token.
            Task nodeTimeoutTask = Task.Delay((int)await this.EvaluateDynamicProperty(treeNode.Timeout ?? -1, typeof(int)).ConfigureAwait(false), this.walkTreeCts.Token);
            actionTasks.Add(nodeTimeoutTask);

            while (actionTasks.Count > 1)
            {
                // Throw if cancellation was requested between actions completing.
                this.walkTreeCts.Token.ThrowIfCancellationRequested();

                Task completedTask = await Task.WhenAny(actionTasks).ConfigureAwait(false);
                actionTasks.Remove(completedTask);

                if (completedTask == nodeTimeoutTask)
                {
                    // Throw on cancellation requested if that's the reason the timeout task completed.
                    this.walkTreeCts.Token.ThrowIfCancellationRequested();

                    // NodeTimeout was hit, throw a special exception to differentiate between timeout and cancellation.
                    throw new TimeoutException("Hit node-level timeout in TreeNodeKey: " + treeNodeKey);
                }

                // Await the completed task to propagate any exceptions.
                // Exceptions are thrown here if the action hit a timeout, was cancelled, or failed.
                await completedTask;
            }
        }

        /// <summary>
        /// Executes the given action. Attempts retries according to the retry policy and timeout.
        /// Returns without throwing exception if the action was completed successfully.
        /// </summary>
        /// <param name="treeNodeKey">The TreeNode's key where the actions are taking place.</param>
        /// <param name="treeActionKey">The TreeAction's key of the action taking place.</param>
        /// <param name="treeAction">The TreeAction object that holds properties of the action.</param>
        /// <param name="actionDefinition">The object holding definitions for the action to execute.</param>
        /// <param name="token">The cancellation token.</param>
        /// <exception cref="ActionTimeoutException">If the action-level timeout was hit.</exception>
        /// <exception cref="OperationCanceledException">If the cancellation token was triggered.</exception>
        internal async Task ExecuteActionWithRetry(
            string treeNodeKey,
            string treeActionKey,
            TreeAction treeAction,
            ActionDefinition actionDefinition,
            CancellationToken token)
        {
            // Initialize values. Default infinite timeout. Default RetryPolicyType.None.
            int retryCount = 0;
            Exception innerException = null;
            Stopwatch stopwatch = new Stopwatch();

            int actionTimeout = (int)await this.EvaluateDynamicProperty(treeAction.Timeout ?? -1, typeof(int)).ConfigureAwait(false);
            RetryPolicyType retryPolicyType = treeAction.RetryPolicy != null ? treeAction.RetryPolicy.Type : RetryPolicyType.None;
            TimeSpan waitTime = treeAction.RetryPolicy != null ? TimeSpan.FromMilliseconds(treeAction.RetryPolicy.MinBackoffMs) : new TimeSpan();

            // Kick off timers.
            Task actionTimeoutTask = Task.Delay(actionTimeout, token);
            stopwatch.Start();

            // Attmpt to ExecuteAction based on RetryPolicy and Timeout.
            // Throw on non-retriable exceptions.
            while (actionTimeout == -1 || stopwatch.ElapsedMilliseconds < actionTimeout)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    await this.ExecuteAction(treeNodeKey, treeActionKey, treeAction, actionDefinition, actionTimeoutTask, token).ConfigureAwait(false);
                    return; // success!
                }
                catch (OperationCanceledException)
                {
                    throw; // non-retriable exception
                }
                catch (ActionTimeoutException)
                {
                    throw; // non-retriable exception
                }
                catch (EvaluateDynamicPropertyException)
                {
                    throw; // non-retriable exception
                }
                catch (Exception e)
                {
                    // Cache exception as innerException in case we need to throw ActionTimeoutException.
                    innerException = e;

                    // Hit retriable exception. Retry according to RetryPolicy.
                    // When retries are exhausted, throw ActionTimeoutException with Exception e as the innerException.
                    switch (retryPolicyType)
                    {
                        case RetryPolicyType.FixedInterval:
                        {
                            // FixedInterval retries every MinBackoffMs until the timeout.
                            // Ex) 200ms, 200ms, 200ms...
                            waitTime = TimeSpan.FromMilliseconds(treeAction.RetryPolicy.MinBackoffMs);
                            break;
                        }
                        case RetryPolicyType.ExponentialBackoff:
                        {
                            // ExponentialBackoff retries every Math.Min(MinBackoffMs * 2^(retryCount), MaxBackoffMs) until the timeout.
                            // Ex) 100ms, 200ms, 400ms...
                            waitTime = TimeSpan.FromMilliseconds(Math.Min(treeAction.RetryPolicy.MaxBackoffMs, waitTime.TotalMilliseconds * 2));
                            break;
                        }
                        case RetryPolicyType.None:
                        default:
                        {
                            // No retries. Break out below to throw non-retriable exception.
                            break;
                        }
                    }
                }

                // Break out if no retry policy set.
                if (retryPolicyType == RetryPolicyType.None)
                {
                    // If the retries have exhausted and the ContinuationOnRetryExhaustion flag is set, commit a new ActionResponse 
                    // with the status set to RetryExhaustedOnAction and return.
                    if (treeAction.ContinuationOnRetryExhaustion)
                    {
                        ActionResponse timeoutResponse = new ActionResponse
                        {
                            Status = "RetryExhaustedOnAction"
                        };

                        await this.CommitActionResponse(treeActionKey, timeoutResponse).ConfigureAwait(false);
                        return;
                    }

                    break;
                }

                // Break out early if we would hit timeout before next retry.
                if (actionTimeout != -1 && stopwatch.ElapsedMilliseconds + waitTime.TotalMilliseconds >= actionTimeout)
                {
                    // If the timeout is hit and the ContinuationOnTimeout flag is set, commit a new ActionResponse 
                    // with the status set to TimeoutOnAction and return.
                    if (treeAction.ContinuationOnTimeout)
                    {
                        ActionResponse timeoutResponse = new ActionResponse
                        {
                            Status = "TimeoutOnAction"
                        };

                        await this.CommitActionResponse(treeActionKey, timeoutResponse).ConfigureAwait(false);
                        return;
                    }

                    break;
                }

                token.ThrowIfCancellationRequested();
                await Task.Delay(waitTime, token).ConfigureAwait(false);
                retryCount++;
            }

            // Retries are exhausted. Throw ActionTimeoutException with executeAction exception as innerException.
            throw new ActionTimeoutException(
                string.Format(
                    "Action did not complete successfully. TreeNodeKey: {0}, TreeActionKey: {1}, ActionName: {2}, RetryCount: {3}, RetryPolicy: {4}",
                    treeNodeKey,
                    treeActionKey,
                    treeAction.Action,
                    retryCount,
                    retryPolicyType),
                innerException);
        }

        /// <summary>
        /// Executes the given actionTask and commits the ActionResponse to forgeState on success.
        /// </summary>
        /// <param name="treeNodeKey">The TreeNode's key where the actions are taking place.</param>
        /// <param name="treeActionKey">The TreeAction's key of the action taking place.</param>
        /// <param name="treeAction">The TreeAction object that holds properties of the action.</param>
        /// <param name="actionDefinition">The object holding definitions for the action to execute.</param>
        /// <param name="actionTimeoutTask">The delay task tied to the action timeout.</param>
        /// <param name="token">The cancellation token.</param>
        /// <exception cref="ActionTimeoutException">If the action-level timeout was hit.</exception>
        /// <exception cref="OperationCanceledException">If the cancellation token was triggered.</exception>
        /// <returns>
        ///     RanToCompletion if the action was completed successfully.
        ///     Exceptions are thrown on timeout, cancellation, or retriable failures.
        /// </returns>
        internal async Task ExecuteAction(
            string treeNodeKey,
            string treeActionKey,
            TreeAction treeAction,
            ActionDefinition actionDefinition,
            Task actionTimeoutTask,
            CancellationToken token)
        {
            // Set up a linked cancellation token to trigger on timeout if ContinuationOnTimeout is set.
            // This ensures the runActionTask gets canceled when Forge timeout is hit.
            CancellationTokenSource actionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            token = treeAction.ContinuationOnTimeout ? actionCts.Token : token;

            // Evaluate the dynamic properties that are used by the actionTask.
            ActionContext actionContext = new ActionContext(
                this.Parameters.SessionId,
                treeNodeKey,
                treeActionKey,
                treeAction.Action,
                await this.EvaluateDynamicProperty(treeAction.Input, actionDefinition.InputType).ConfigureAwait(false),
                await this.EvaluateDynamicProperty(treeAction.Properties, null).ConfigureAwait(false),
                this.Parameters.UserContext,
                token,
                this.Parameters.ForgeState
            );

            // Instantiate the BaseAction-derived ActionType class and invoke the RunAction method on it.
            var actionObject = Activator.CreateInstance(actionDefinition.ActionType);
            MethodInfo method = typeof(BaseAction).GetMethod("RunAction");
            Task<ActionResponse> runActionTask = (Task<ActionResponse>) method.Invoke(actionObject, new object[] { actionContext });

            // Await for the first completed task between our runActionTask and the timeout task.
            // This allows us to continue without awaiting the runActionTask upon timeout.
            var completedTask = await Task.WhenAny(runActionTask, actionTimeoutTask).ConfigureAwait(false);

            if (completedTask == actionTimeoutTask)
            {
                // Throw on cancellation requested if that's the reason the timeout task completed.
                token.ThrowIfCancellationRequested();

                // If the timeout is hit and the ContinuationOnTimeout flag is set, commit a new ActionResponse 
                // with the status set to TimeoutOnAction and return.
                if (treeAction.ContinuationOnTimeout)
                {
                    // Trigger linked cancellation token before continuing to ensure the runActionTask gets cancelled.
                    actionCts.Cancel();

                    ActionResponse timeoutResponse = new ActionResponse
                    {
                        Status = "TimeoutOnAction"
                    };

                    await this.CommitActionResponse(treeActionKey, timeoutResponse).ConfigureAwait(false);
                    return;
                }

                // ActionTimeout has been hit. Throw special exception to indicate this.
                throw new ActionTimeoutException(string.Format(
                    "ActionTimeoutTask timed out before Action could complete. TreeNodeKey: {0}, TreeActionKey: {1}, ActionName: {2}.",
                    treeNodeKey,
                    treeActionKey,
                    treeAction.Action));
            }
            else
            {
                // Handle the completed runActionTask.
                if (runActionTask.Status == TaskStatus.RanToCompletion)
                {
                    await this.CommitActionResponse(treeActionKey, await runActionTask).ConfigureAwait(false);
                }

                // Await the completed task to propagate any exceptions.
                // Exceptions are thrown here if the action hit a timeout, was cancelled, or failed.
                await runActionTask;
            }
        }

        /// <summary>
        /// Iterates through the given schema object, evaluating any Roslyn expressions found in the values.
        /// When a knownType is given, Forge will instantiate that type instead of a dynamic object.
        /// String properties matching the <see cref="RoslynRegex"/> represent a code-snippet that will be evaluated.
        /// </summary>
        /// <param name="schemaObj">The object given from the schema.</param>
        /// <param name="knownType">The type of the object being evaluated. Null here represents an unknown type that will be evaluated dynamically.</param>
        /// <exception cref="EvaluateDynamicPropertyException">If exceptions are thrown while evaluating the dynamic property.</exception>
        /// <returns>The properties after evaluation.</returns>
        public async Task<object> EvaluateDynamicProperty(dynamic schemaObj, Type knownType)
        {
            try
            {
                if (schemaObj == null)
                {
                    return null;
                }
                else if (schemaObj is string && schemaObj.StartsWith(RoslynLeadingText))
                {
                    // Case when schema property is a Roslyn expression.
                    // Evaluate it as either the knownType if it exists, the <type> embeded in the RoslynRegex, or a string by default, in that order.
                    Match result = RoslynRegex.Match(schemaObj);
                    if (result.Success)
                    {
                        string typeStr = string.IsNullOrWhiteSpace(result.Groups[2].Value) ? "String" : result.Groups[2].Value;
                        Type type = knownType ?? Type.GetType("System." + typeStr);

                        MethodInfo method = typeof(ExpressionExecutor).GetMethod("Execute");
                        MethodInfo genericMethod = method.MakeGenericMethod(type);
                        string expression = schemaObj.Substring(result.Groups[0].Value.Length);
                        var res = await (dynamic) genericMethod.Invoke(this.expressionExecutor, new object[] { expression });
                        return res;
                    }
                }
                else if (schemaObj is string)
                {
                    foreach (var kvp in this.Parameters.ExternalExecutors)
                    {
                        string regexString = kvp.Key;
                        Func<string, CancellationToken, Task<object>> externalExecutor = kvp.Value;
                        
                        if (schemaObj.StartsWith(regexString))
                        {
                            // Case when schema property matches an external executor.
                            // Evaluate it and return the result as the knownType if it exists or as a string otherwise.
                            var result = await externalExecutor(schemaObj.Substring(regexString.Length), this.walkTreeCts.Token).ConfigureAwait(false);
                            return knownType != null ? Convert.ChangeType(result, knownType) : result;
                        }
                    }

                    return schemaObj;
                }
                else if (schemaObj is JObject)
                {
                    // Case when schema object has properties (i.e. is a dictionary).
                    // Instantiate and use the knownType if given, then evaluate each property using recursion.
                    dynamic knownObj = Activator.CreateInstance(knownType ?? typeof(object));

                    IDictionary<string, dynamic> propertyValues = schemaObj.ToObject<IDictionary<string, dynamic>>();
                    foreach (string key in new List<string>(propertyValues.Keys))
                    {
                        if (knownType != null)
                        {
                            var prop = knownType.GetProperty(key);
                            prop.SetValue(knownObj, await this.EvaluateDynamicProperty(propertyValues[key], prop.PropertyType).ConfigureAwait(false));
                        }
                        else
                        {
                            propertyValues[key] = await this.EvaluateDynamicProperty(propertyValues[key], null).ConfigureAwait(false);
                        }
                    }

                    return knownType != null ? knownObj : (dynamic)JObject.FromObject(propertyValues);
                }
                else if (schemaObj is JArray)
                {
                    // Case when schema object is an array.
                    // Create an array with the knownType if given, then use recursion to evaluate each index.
                    dynamic knownObj = Activator.CreateInstance(knownType ?? typeof(object[]), schemaObj.Count);

                    for (int i = 0; i < schemaObj.Count; i++)
                    {
                        if (knownType != null)
                        {
                            knownObj.SetValue(await this.EvaluateDynamicProperty(schemaObj[i], knownType.GetElementType()).ConfigureAwait(false), i);
                        }
                        else
                        {
                            schemaObj[i] = await this.EvaluateDynamicProperty(schemaObj[i], null).ConfigureAwait(false);
                        }
                    }

                    return knownType != null ? knownObj : schemaObj;
                }
                else if (schemaObj is JValue)
                {
                    // Case when schema object is a value type.
                    // Return the value as the knownType if given.
                    return knownType != null ? Convert.ChangeType(schemaObj.Value, knownType) : schemaObj.Value;
                }
                else
                {
                    // Case when schema object is a value type.
                    // Return the value as the knownType if given.
                    return knownType != null ? Convert.ChangeType(schemaObj, knownType) : schemaObj;
                }
            }
            catch (OperationCanceledException)
            {
                throw; // rethrow on cancelled.
            }
            catch (Exception e)
            {
                throw new EvaluateDynamicPropertyException(
                    string.Format("EvaluateDynamicProperty failed to parse schemaObj: {0}, knownType: {1}.", schemaObj?.ToString(), knownType),
                    e);
            }

            return null;
        }

        /// <summary>
        /// Commits the ActionResponse to the forgeState.
        /// This allows the ActionResponses to be dynamically referenced in the ForgeTree through ITreeSession interface.
        /// </summary>
        /// <param name="treeActionKey">The TreeAction's key of the action that was executed.</param>
        /// <param name="actionResponse">The action response object returned from the action.</param>
        private async Task CommitActionResponse(string treeActionKey, ActionResponse actionResponse)
        {
            await this.Parameters.ForgeState.Set<ActionResponse>(treeActionKey + ActionResponseSuffix, actionResponse).ConfigureAwait(false);
            await this.CommitLastTreeAction(treeActionKey).ConfigureAwait(false);
        }

        /// <summary>
        /// Commits the current tree node to the forgeState.
        /// The wrapper class has access to this state, allowing it to rehydrate/retry on failures if desired.
        /// </summary>
        /// <param name="treeNodeKey">The TreeNode's key that tree walker is currently walking.</param>
        private Task CommitCurrentTreeNode(string treeNodeKey)
        {
            // TODO: Consider adding a "nodeStep" to save if we already completed the BeforeVisitNode step and should skip straight to VisitNode.
            return this.Parameters.ForgeState.Set<string>(CurrentTreeNodeSuffix, treeNodeKey);
        }

        /// <summary>
        /// Commits the last tree action to the forgeState.
        /// </summary>
        /// <param name="treeActionKey">The TreeAction's key of the last action that was committed.</param>
        private Task CommitLastTreeAction(string treeActionKey)
        {
            return this.Parameters.ForgeState.Set<string>(LastTreeActionSuffix, treeActionKey);
        }

        /// <summary>
        /// Initializes the actionsMap from the given assembly.
        /// This map is generated using reflection to find all the classes with the applied ForgeActionAttribute from the given Assembly.
        /// </summary>
        /// <param name="forgeActionsAssembly">The Assembly containing ForgeActionAttribute tagged classes.</param>
        /// <param name="actionsMap">The map of string ActionNames to ActionDefinitions.</param>
        public static void GetActionsMapFromAssembly(Assembly forgeActionsAssembly, out Dictionary<string, ActionDefinition> actionsMap)
        {
            actionsMap = new Dictionary<string, ActionDefinition>();

            if (forgeActionsAssembly == null)
            {
                return;
            }

            foreach (Type type in forgeActionsAssembly.GetExportedTypes())
            {
                // Find all classes with the applied ForgeActionAttribute.
                ForgeActionAttribute forgeAction = (ForgeActionAttribute) type.GetCustomAttribute(typeof(ForgeActionAttribute), false);
                if (forgeAction != null)
                {
                    // Confirm that ForgeActionAttribute is attached to only BaseAction classes and there are no duplicates.
                    Type derived = type;
                    bool isBaseAction = false;
                    do
                    {
                        if (derived == typeof(BaseAction))
                        {
                            isBaseAction = true;
                            break;
                        }

                        derived = derived.BaseType;
                    } while (derived != null);

                    if (isBaseAction)
                    {
                        actionsMap.Add(type.Name, new ActionDefinition() { ActionType = type, InputType = forgeAction.InputType });
                        continue;
                    }
                    else
                    {
                        throw new CustomAttributeFormatException(
                            string.Format(
                                "The given type: {0} must implement the BaseAction abstract class in order to apply the ForgeActionAttribute.",
                                type.ToString()));
                    }
                }
            }
        }
    }
}