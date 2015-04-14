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

                // This loop will run until a rebase result has been set.
                while (rebaseResult == null)
                {
                    RebaseStepInfo stepToApplyInfo;
                    RebaseAction action = NextRebaseAction(out stepToApplyInfo, repository, rebaseOperationHandle, isStarting);
                    isStarting = false;

                    switch (action)
                    {
                        case RebaseAction.ApplyStep:
                            rebaseResult = ApplyRebaseStep(rebaseOperationHandle,
                                                           repository,
                                                           committer,
                                                           options,
                                                           ref gitCheckoutOpts,
                                                           stepToApplyInfo);
                            break;
                        case RebaseAction.Finish:
                            rebaseResult = FinishRebase(rebaseOperationHandle, committer, rebaseResult);
                            break;
                        default:
                            // If we arrived in this else block, it means there is a programing error.
                            throw new LibGit2SharpException("Unexpected Next Action. Program error.");
                    }
                }

                return rebaseResult;
            }
        }

        private static RebaseResult FinishRebase(RebaseSafeHandle rebaseOperationHandle, Signature committer, RebaseResult rebaseResult)
        {
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
            return rebaseResult;
        }

        private static RebaseResult ApplyRebaseStep(RebaseSafeHandle rebaseOperationHandle, Repository repository, Signature committer, RebaseOptions options, ref GitCheckoutOpts gitCheckoutOpts, RebaseStepInfo stepToApplyInfo)
        {
            RebaseResult rebaseResult = null;

            // Report the rebase step we are about to perform.
            if (options.RebaseStepStarting != null)
            {
                options.RebaseStepStarting(new BeforeRebaseStepInfo(stepToApplyInfo));
            }

            // Perform the rebase step
            GitRebaseOperation rebaseOpReport = Proxy.git_rebase_next(rebaseOperationHandle, ref gitCheckoutOpts);

            // Verify that the information from the native library is consistent.
            VerifyRebaseOp(rebaseOpReport, stepToApplyInfo);

            // Handle the result
            switch (stepToApplyInfo.Type)
            {
                case RebaseStepOperation.Pick:
                    rebaseResult = ApplyPickStep(rebaseOperationHandle, repository, committer, options, stepToApplyInfo);
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

            return rebaseResult;
        }

        private static RebaseResult ApplyPickStep(RebaseSafeHandle rebaseOperationHandle, Repository repository, Signature committer, RebaseOptions options, RebaseStepInfo stepToApplyInfo)
        {
            RebaseResult rebaseResult = null;

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

            return rebaseResult;
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

            if (stepToApplyIndex < totalStepCount)
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
            else if (stepToApplyIndex == totalStepCount)
            {
                action = RebaseAction.Finish;
                stepToApply = null;
            }
            else
            {
                // This is an unexpected condition - should not happen in normal operation.
                throw new LibGit2SharpException(string.Format("Current step ({0}) is larger than the total number of steps ({1})",
                                                stepToApplyIndex, totalStepCount));
            }

            return action;
        }
    }
}
