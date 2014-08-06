﻿using System;
using System.Linq;
using System.Web.Mvc;
using Dialogue.Logic.Models;
using Dialogue.Logic.Models.ViewModels;
using Dialogue.Logic.Services;

namespace Dialogue.Logic.Controllers
{
    public class DialogueVoteSurfaceController : BaseSurfaceController
    {
        [HttpPost]
        [Authorize]
        public ActionResult PostVote(VoteViewModel voteUpViewModel)
        {
            if (Request.IsAjaxRequest())
            {
                // Quick check to see if user is locked out, when logged in
                if (CurrentMember.IsLockedOut | !CurrentMember.IsApproved)
                {
                    ServiceFactory.MemberService.LogOff();
                    throw new Exception(Lang("Errors.NoAccess"));
                }


                using (var unitOfWork = UnitOfWorkManager.NewUnitOfWork())
                {
                    // Firstly get the post
                    var post = ServiceFactory.PostService.Get(voteUpViewModel.Post);

                    var allowedToVote = (CurrentMember.Id != post.MemberId &&
                                    CurrentMember.TotalPoints > Settings.AmountOfPointsBeforeAUserCanVote &&
                                    post.Votes.All(x => x.MemberId != CurrentMember.Id));

                    if (allowedToVote)
                    {
                        // Now get the current user
                        var voter = CurrentMember;

                        // Also get the user that wrote the post
                        var postWriter = ServiceFactory.MemberService.Get(post.MemberId);

                        // Mark the post up or down
                        var returnValue = 0;
                        if (voteUpViewModel.IsVoteUp)
                        {
                            returnValue = MarkPostUpOrDown(post, postWriter, voter, PostType.Positive);
                        }
                        else
                        {
                            returnValue = MarkPostUpOrDown(post, postWriter, voter, PostType.Negative);
                        }

                        try
                        {
                            unitOfWork.Commit();
                            return Content(returnValue.ToString());
                        }
                        catch (Exception ex)
                        {
                            unitOfWork.Rollback();
                            LogError(ex);
                        }
                    }
                    else
                    {
                        return Content(post.VoteCount.ToString());
                    }

                }
            }
            throw new Exception(Lang("Errors.GenericMessage"));
        }

        private int MarkPostUpOrDown(Post post, Member postWriter, Member voter, PostType postType)
        {
            // Check this user is not the post owner
            if (voter.Id != postWriter.Id)
            {
                // Not the same person, now check they haven't voted on this post before
                if (post.Votes.All(x => x.MemberId != CurrentMember.Id))
                {

                    // Points to add or subtract to a user
                    var usersPoints = (postType == PostType.Negative) ?
                                        (-Settings.PointsDeductedForNegativeVote) : (Settings.PointsAddedForPositiveVote);

                    // Update the users points who wrote the post
                    ServiceFactory.MemberPointsService.Add(new MemberPoints { Points = usersPoints, Member = postWriter, MemberId = postWriter.Id });

                    // Update the post with the new vote of the voter
                    var vote = new Vote
                    {
                        Post = post,
                        Member = voter,
                        MemberId = voter.Id,
                        Amount = (postType == PostType.Negative) ? (-1) : (1),
                        VotedByMember = CurrentMember,
                        DateVoted = DateTime.Now
                    };
                    ServiceFactory.VoteService.Add(vote);

                    // Update the post with the new points amount
                    var newPointTotal = (postType == PostType.Negative) ? (post.VoteCount - 1) : (post.VoteCount + 1);
                    post.VoteCount = newPointTotal;

                    var allVotes = post.Votes.ToList();
                    if (postType == PostType.Positive)
                    {
                        return allVotes.Count(x => x.Amount > 0);
                    }
                    return allVotes.Count(x => x.Amount < 0);
                }
            }
            return 0;
        }

        private enum PostType
        {
            Positive,
            Negative,
        };

        [HttpPost]
        [Authorize]
        public void MarkAsSolution(MarkAsSolutionViewModel markAsSolutionViewModel)
        {
            if (Request.IsAjaxRequest())
            {
                // Quick check to see if user is locked out, when logged in
                if (CurrentMember.IsLockedOut | !CurrentMember.IsApproved)
                {
                    ServiceFactory.MemberService.LogOff();
                    throw new Exception(Lang("Errors.NoAccess"));
                }


                using (var unitOfWork = UnitOfWorkManager.NewUnitOfWork())
                {
                    // Firstly get the post
                    var post = ServiceFactory.PostService.Get(markAsSolutionViewModel.Post);

                    // Check the member marking owns the topic
                    if (CurrentMember.Id == post.Topic.MemberId)
                    {
                        // Person who created the solution post
                        var solutionWriter = ServiceFactory.MemberService.Get(post.MemberId);

                        // Get the post topic
                        var topic = post.Topic;

                        // Now get the current user
                        var marker = CurrentMember;
                        try
                        {
                            var solved = ServiceFactory.TopicService.SolveTopic(topic, post, marker, solutionWriter);

                            if (solved)
                            {
                                unitOfWork.Commit();
                            }
                        }
                        catch (Exception ex)
                        {
                            unitOfWork.Rollback();
                            LogError(ex);
                            throw new Exception(Lang("Errors.GenericMessage"));
                        }                        
                    }
                    else
                    {
                        throw new Exception(Lang("Errors.Generic"));
                    }

                }
            }
        }


        //[HttpPost]
        //public PartialViewResult GetVoters(VoteUpViewModel voteUpViewModel)
        //{
        //    if (Request.IsAjaxRequest())
        //    {
        //        var post = _postService.Get(voteUpViewModel.Post);
        //        var positiveVotes = post.Votes.Where(x => x.Amount > 0);
        //        var viewModel = new ShowVotersViewModel { Votes = positiveVotes.ToList() };
        //        return PartialView(viewModel);
        //    }
        //    return null;
        //}

        //[HttpPost]
        //public PartialViewResult GetVotes(VoteUpViewModel voteUpViewModel)
        //{
        //    if (Request.IsAjaxRequest())
        //    {
        //        var post = _postService.Get(voteUpViewModel.Post);
        //        var positiveVotes = post.Votes.Count(x => x.Amount > 0);
        //        var negativeVotes = post.Votes.Count(x => x.Amount <= 0);
        //        var viewModel = new ShowVotesViewModel { DownVotes = negativeVotes, UpVotes = positiveVotes };
        //        return PartialView(viewModel);
        //    }
        //    return null;
        //}
    }
}