using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using LibGit2Sharp.Core;
using LibGit2Sharp.Core.Handles;

namespace LibGit2Sharp
{
    internal class RebaseOperationImpl
    {
        private enum RebaseAction
        {
            ApplyStep,
            Finish,
            Finished,
        };

        /// <summary>
        /// Run a rebase to completion, a conflict, or a requested stop point.
        /// </summary>
        /// <param name="rebaseOperationHandle">Handle to the rebase operation.</param>
        /// <param name="repository">Repository in which rebase operation is being run.</param>
        /// <param name="committer">Committer signature to use for the rebased commits.</param>
        /// <param name="options">Options controlling rebase behavior.</param>
        /// <param name="isStarting">flag to indicate if this is being called as part of starting a rebase sequence.</param>
        /// <returns>RebaseResult - describing the result of the rebase operation.</returns>
        public static RebaseResult Run(RebaseSafeHandle rebaseOperationHandle,
            Repository repository,
            Signature committer,
            RebaseOptions options,
            bool isStarting)
        {
            Ensure.ArgumentNotNull(rebaseOperationHandle, "rebaseOperationHandle");
            Ensure.ArgumentNotNull(repository, "repository");
            Ensure.ArgumentNotNull(committer, "committer");
            Ensure.ArgumentNotNull(options, "options");

            using (GitCheckoutOptsWrapper checkoutOptionsWrapper = new GitCheckoutOptsWrapper(options))
            {
                GitCheckoutOpts gitCheckoutOpts = checkoutOptionsWrapper.Options;
                RebaseResult rebaseResult = null;

                RebaseAction action;
                RebaseStepInfo stepToApplyInfo;

                // This loop will run until either:
                //   1) All steps have been run or
                //   2) rebaseResult is set - indicating that the current
                //      sequence should be stopped and a result needs to be
                //      reported.
                while ((action = NextRebaseAction(out stepToApplyInfo, repository, rebaseOperationHandle, isStarting)) == RebaseAction.ApplyStep)
                {
                    isStarting = false;

                    // Report the rebase step we are about to perform.
                    if (options.RebaseStepStarting != null)
                    {
                        options.RebaseStepStarting(new BeforeRebaseStepInfo(stepToApplyInfo));
                    }

                    // Perform the rebase step
                    GitRebaseOperation rebaseOpReport = Proxy.git_rebase_next(rebaseOperationHandle, ref gitCheckoutOpts);

                    VerifyRebaseOp(rebaseOpReport, stepToApplyInfo);

                    // Handle the result
                    switch (stepToApplyInfo.Type)
                    {
                        case RebaseStepOperation.Pick:
                            // commit and continue.
                            if (repository.Index.IsFullyMerged)
                            {
                                Proxy.GitRebaseCommitResult rebase_commit_result = Proxy.git_rebase_commit(rebaseOperationHandle, null, committer);

                                // Report that we just completed the step
                                if (options.RebaseStepCompleted != null)
                                {
                                    if (rebase_commit_result.WasPatchAlreadyApplied)
                                    {
                                        options.RebaseStepCompleted(new AfterRebaseStepInfo(stepToApplyInfo));
                                    }
                                    else
                                    {
                                        options.RebaseStepCompleted(new AfterRebaseStepInfo(stepToApplyInfo, repository.Lookup<Commit>(new ObjectId(rebase_commit_result.CommitId))));
                                    }
                                }
                            }
                            else
                            {
                                rebaseResult = new RebaseResult(RebaseStatus.Conflicts,
                                                                stepToApplyInfo.CurrentStep,
                                                                stepToApplyInfo.TotalStepCount,
                                                                null);
                            }
                            break;
                        case RebaseStepOperation.Squash:
                        case RebaseStepOperation.Edit:
                        case RebaseStepOperation.Exec:
                        case RebaseStepOperation.Fixup:
                        case RebaseStepOperation.Reword:
                            // These operations are not yet supported by lg2.
                            throw new LibGit2SharpException(string.Format(
                                "Rebase Operation Type ({0}) is not currently supported in LibGit2Sharp.",
                                stepToApplyInfo.Type));
                        default:
                            throw new ArgumentException(string.Format(
                                "Unexpected Rebase Operation Type: {0}", stepToApplyInfo.Type));
                    }

                    // If we have not generated a result that needs to be
                    // reported, move to the next step.
                    if (rebaseResult != null)
                    {
                        break;
                    }
                }

                // If the step being applied is equal to the total step count,
                // that means all steps have been run and we are finished.
                if (action == RebaseAction.Finish)
                {
                    Debug.Assert(rebaseResult == null);

                    long totalStepCount = Proxy.git_rebase_operation_entrycount(rebaseOperationHandle);
                    GitRebaseOptions gitRebaseOptions = new GitRebaseOptions()
                    {
                        version = 1,
                    };

                    // Rebase is completed!
                    Proxy.git_rebase_finish(rebaseOperationHandle, committer, gitRebaseOptions);
                    rebaseResult = new RebaseResult(RebaseStatus.Complete,
                                                    totalStepCount,
                                                    totalStepCount,
                                                    null);
                }

                return rebaseResult;
            }
        }

        /// <summary>
        /// Verify that the information in a GitRebaseOperation and a RebaseStepInfo agree
        /// </summary>
        /// <param name="rebaseOpReport"></param>
        /// <param name="stepInfo"></param>
        private static void VerifyRebaseOp(GitRebaseOperation rebaseOpReport, RebaseStepInfo stepInfo)
        {
            // The step reported via querying by index and the step returned from git_rebase_next
            // should be the same
            if (rebaseOpReport == null ||
                new ObjectId(rebaseOpReport.id) != stepInfo.Commit.Id ||
                rebaseOpReport.type != stepInfo.Type)
            {
                // This is indicative of a program error - should never happen.
                throw new LibGit2SharpException("Unexpected step info reported by running rebase step.");
            }
        }

        private static RebaseAction NextRebaseAction(
            out RebaseStepInfo stepToApply,
            Repository repository,
            RebaseSafeHandle rebaseOperationHandle,
            bool isStarting)
        {
            RebaseAction action;

            // stepBeingApplied indicates the step that will be applied by by git_rebase_next.
            // The current step does not get incremented until git_rebase_next (except on
            // the initial step), but we want to report the step that will be applied.
            long stepToApplyIndex = Proxy.git_rebase_operation_current(rebaseOperationHandle);
            if (!isStarting)
            {
                stepToApplyIndex++;
            }

            long totalStepCount = Proxy.git_rebase_operation_entrycount(rebaseOperationHandle);

            if (stepToApplyIndex == totalStepCount)
            {
                action = RebaseAction.Finish;
                stepToApply = null;
            }
            else
            {
                action = RebaseAction.ApplyStep;

                GitRebaseOperation rebaseOp = Proxy.git_rebase_operation_byindex(rebaseOperationHandle, stepToApplyIndex);
                ObjectId idOfCommitBeingRebased = new ObjectId(rebaseOp.id);
                stepToApply = new RebaseStepInfo(rebaseOp.type,
                     repository.Lookup<Commit>(idOfCommitBeingRebased),
                     LaxUtf8NoCleanupMarshaler.FromNative(rebaseOp.exec),
                     stepToApplyIndex,
                     totalStepCount);
            }

            return action;
        }
    }
}
