﻿namespace Dialogue.Logic.Controllers
{
    using System;
    using System.Linq;
    using System.Web.Mvc;
    using Application;
    using Application.Akismet;
    using Constants;
    using Mapping;
    using Models;
    using Models.ViewModels;
    using Routes;
    using Umbraco.Core.Models;
    using Umbraco.Web.Models;
    using System.Collections.Generic;
    using System.Text;

    public partial class DialogueTopicController : DialogueBaseController
    {
        private readonly IMemberGroup _membersGroup;

        public DialogueTopicController()
        {
            _membersGroup = (CurrentMember == null ? MemberService.GetGroupByName(AppConstants.GuestRoleName) : CurrentMember.Groups.FirstOrDefault());
        }

        /// <summary>
        /// Used to render the Topic (virtual node)
        /// </summary>
        /// <param name="model"></param>
        /// <param name="topicname">
        /// The topic slug which we use to look up the topic
        /// </param>
        /// <param name="p"></param>
        /// <returns></returns>
        public ActionResult Show(RenderModel model, string topicname, int? p = null)
        {
            var tagPage = model.Content as DialogueVirtualPage;
            if (tagPage == null)
            {
                throw new InvalidOperationException("The RenderModel.Content instance must be of type " + typeof(DialogueVirtualPage));
            }

            if (string.IsNullOrEmpty(topicname))
            {
                return ErrorToHomePage(Lang("Errors.GenericMessage"));
            }

            // Set the page index
            var pageIndex = AppHelpers.ReturnCurrentPagingNo();

            using (var unitOfWork = UnitOfWorkManager.NewUnitOfWork())
            {
                // Get the topic
                var topic = TopicService.GetTopicBySlug(topicname);

                if (topic != null)
                {
                    // Note: Don't use topic.Posts as its not a very efficient SQL statement
                    // Use the post service to get them as it includes other used entities in one
                    // statement rather than loads of sql selects

                    var sortQuerystring = Request.QueryString[AppConstants.PostOrderBy];
                    var orderBy = !string.IsNullOrEmpty(sortQuerystring) ?
                                              AppHelpers.EnumUtils.ReturnEnumValueFromString<PostOrderBy>(sortQuerystring) : PostOrderBy.Standard;

                    // Store the amount per page
                    var amountPerPage = Settings.PostsPerPage;
                    var hasCommentHash = Request.Url != null &&
                                         Request.Url.PathAndQuery.IndexOf("#comment-",
                                             StringComparison.CurrentCultureIgnoreCase) >= 0;

                    if (sortQuerystring == PostOrderBy.All.ToString() || hasCommentHash)
                    {
                        // Overide to show all posts
                        amountPerPage = int.MaxValue;
                    }


                    // Get the posts
                    var posts = PostService.GetPagedPostsByTopic(pageIndex,
                                                                  amountPerPage,
                                                                  int.MaxValue,
                                                                  topic.Id,
                                                                  orderBy);

                    // Get the permissions for the category that this topic is in
                    var permissions = PermissionService.GetPermissions(topic.Category, _membersGroup, MemberService, CategoryPermissionService);

                    // If this user doesn't have access to this topic then
                    // redirect with message
                    if (permissions[AppConstants.PermissionDenyAccess].IsTicked)
                    {
                        return ErrorToHomePage(Lang("Errors.NoPermission"));
                    }

                    // See if the user has subscribed to this topic or not
                    var isSubscribed = UserIsAuthenticated && (TopicNotificationService.GetByUserAndTopic(CurrentMember, topic).Any());

                    // Populate the view model for this page
                    var viewModel = new ShowTopicViewModel(model.Content)
                    {
                        Topic = topic,
                        PageIndex = posts.PageIndex,
                        TotalCount = posts.TotalCount,
                        Permissions = permissions,
                        User = CurrentMember,
                        IsSubscribed = isSubscribed,
                        UserHasAlreadyVotedInPoll = false,
                        TotalPages = posts.TotalPages
                    };

                    // Get all votes for all the posts
                    var postIds = posts.Select(x => x.Id).ToList();
                    var allPostVotes = VoteService.GetAllVotesForPosts(postIds);

                    // Get all favourites for this user
                    viewModel.Favourites = new List<Favourite>();
                    if (CurrentMember != null)
                    {
                        viewModel.Favourites.AddRange(FavouriteService.GetAllByMember(CurrentMember.Id));
                    }

                    // Map the topic Start
                    // Get the topic starter post
                    var topicStarter = PostService.GetTopicStarterPost(topic.Id);
                    viewModel.TopicStarterPost = PostMapper.MapPostViewModel(permissions, topicStarter, CurrentMember, Settings, topic, topicStarter.Votes.ToList(), viewModel.Favourites);

                    // Map the posts to the posts viewmodel
                    viewModel.Posts = new List<ViewPostViewModel>();
                    foreach (var post in posts)
                    {
                        var postViewModel = PostMapper.MapPostViewModel(permissions, post, CurrentMember, Settings, topic, allPostVotes, viewModel.Favourites);
                        viewModel.Posts.Add(postViewModel);
                    }

                    // If there is a quote querystring
                    var quote = Request["quote"];
                    if (!string.IsNullOrEmpty(quote))
                    {
                        try
                        {
                            // Got a quote
                            var postToQuote = PostService.Get(new Guid(quote));
                            viewModel.PostContent = postToQuote.PostContent;
                        }
                        catch (Exception ex)
                        {
                            LogError(ex);
                        }
                    }

                    // See if the topic has a poll, and if so see if this user viewing has already voted
                    if (topic.Poll != null)
                    {
                        // There is a poll and a user
                        // see if the user has voted or not
                        var votes = topic.Poll.PollAnswers.SelectMany(x => x.PollVotes).ToList();
                        if (UserIsAuthenticated)
                        {
                            viewModel.UserHasAlreadyVotedInPoll = (votes.Count(x => x.MemberId == CurrentMember.Id) > 0);
                        }
                        viewModel.TotalVotesInPoll = votes.Count();
                    }

                    // update the topic view count only if this topic doesn't belong to the user looking at it
                    var addView = true;
                    if (UserIsAuthenticated && CurrentMember.Id != topic.MemberId)
                    {
                        addView = false;
                    }

                    if (!AppHelpers.UserIsBot() && addView)
                    {
                        // Cool, user doesn't own this topic
                        topic.Views = (topic.Views + 1);
                        try
                        {
                            unitOfWork.Commit();
                        }
                        catch (Exception ex)
                        {
                            LogError(ex);
                        }
                    }

                    return View(PathHelper.GetThemeViewPath("Topic"), viewModel);
                }

            }
            return ErrorToHomePage(Lang("Errors.GenericMessage"));
        }



        [HttpPost]
        [Authorize]
        public void ApproveTopic(ApproveTopicViewModel model)
        {
            if (Request.IsAjaxRequest() && User.IsInRole(AppConstants.AdminRoleName))
            {
                using (var unitOfWork = UnitOfWorkManager.NewUnitOfWork())
                {
                    var topic = TopicService.Get(model.Id);
                    topic.Pending = false;
                    try
                    {
                        unitOfWork.Commit();
                    }
                    catch (Exception ex)
                    {
                        unitOfWork.Rollback();
                        LogError(ex);
                        throw ex;
                    }
                }
            }
        }

        [HttpPost]
        public PartialViewResult AjaxMorePosts(GetMorePostsViewModel getMorePostsViewModel)
        {
            // Get the topic
            var topic = TopicService.Get(getMorePostsViewModel.TopicId);

            // Get the permissions for the category that this topic is in
            var permissions = PermissionService.GetPermissions(topic.Category, _membersGroup, MemberService, CategoryPermissionService);

            // If this user doesn't have access to this topic then just return nothing
            if (permissions[AppConstants.PermissionDenyAccess].IsTicked)
            {
                return null;
            }

            var orderBy = !string.IsNullOrEmpty(getMorePostsViewModel.Order) ?
                                      AppHelpers.EnumUtils.ReturnEnumValueFromString<PostOrderBy>(getMorePostsViewModel.Order) : PostOrderBy.Standard;



            var viewModel = new ShowMorePostsViewModel
            {
                Topic = topic,
                Permissions = permissions,
                User = CurrentMember
            };

            // Map the posts to the posts viewmodel

            // Get all favourites for this user
            var favourites = new List<Favourite>();
            if (CurrentMember != null)
            {
                favourites.AddRange(FavouriteService.GetAllByMember(CurrentMember.Id));
            }

            // Get the posts
            var posts = PostService.GetPagedPostsByTopic(getMorePostsViewModel.PageIndex, Settings.PostsPerPage, int.MaxValue, topic.Id, orderBy);

            // Get all votes for all the posts
            var postIds = posts.Select(x => x.Id).ToList();
            var allPostVotes = VoteService.GetAllVotesForPosts(postIds);

            viewModel.Posts = new List<ViewPostViewModel>();
            foreach (var post in posts)
            {
                var postViewModel = PostMapper.MapPostViewModel(permissions, post, CurrentMember, Settings, topic, allPostVotes, favourites);
                viewModel.Posts.Add(postViewModel);
            }

            return PartialView(PathHelper.GetThemePartialViewPath("AjaxMorePosts"), viewModel);
        }

        [ChildActionOnly]
        public PartialViewResult GetTopicBreadcrumb(Topic topic)
        {
            var category = CategoryService.Get(topic.CategoryId, true);
            var viewModel = new BreadCrumbViewModel
            {
                Categories = category.ParentCategories,
                Topic = topic
            };
            if (!viewModel.Categories.Any())
            {
                viewModel.Categories.Add(topic.Category);
            }
            return PartialView(PathHelper.GetThemePartialViewPath("GetTopicBreadcrumb"), viewModel);
        }

        public PartialViewResult LatestTopics(int? p)
        {
            using (UnitOfWorkManager.NewUnitOfWork())
            {
                // Set the page index
                var pageIndex = p ?? 1;

                // Get the topics
                var topics = TopicService.GetRecentTopics(pageIndex,
                                                           Dialogue.Settings().TopicsPerPage,
                                                           DialogueConfiguration.Instance.ActiveTopicsListSize);

                // Get all the categories for this topic collection
                var categories = topics.Select(x => x.Category).Distinct();

                // create the view model
                var viewModel = new ActiveTopicsViewModel
                {
                    Topics = topics,
                    AllPermissionSets = new Dictionary<Category, PermissionSet>(),
                    PageIndex = pageIndex,
                    TotalCount = topics.TotalCount,
                    User = CurrentMember
                };

                // loop through the categories and get the permissions
                foreach (var category in categories)
                {
                    var permissionSet = PermissionService.GetPermissions(category, _membersGroup, MemberService, CategoryPermissionService);
                    viewModel.AllPermissionSets.Add(category, permissionSet);
                }
                return PartialView(PathHelper.GetThemePartialViewPath("LatestTopics"), viewModel);
            }
        }


        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public ActionResult Create(CreateTopicViewModel topicViewModel)
        {
            if (ModelState.IsValid)
            {
                // Quick check to see if user is locked out, when logged in
                if (CurrentMember.IsLockedOut || CurrentMember.DisablePosting == true || !CurrentMember.IsApproved)
                {
                    MemberService.LogOff();
                    return ErrorToHomePage(Lang("Errors.NoPermission"));
                }

                var successfullyCreated = false;
                var moderate = false;
                Category category;
                var topic = new Topic();

                using (var unitOfWork = UnitOfWorkManager.NewUnitOfWork())
                {


                    // Before we do anything DB wise, check it contains no bad links
                    if (BannedLinkService.ContainsBannedLink(topicViewModel.TopicContent))
                    {
                        ShowMessage(new GenericMessageViewModel
                        {
                            Message = Lang("Errors.BannedLink"),
                            MessageType = GenericMessages.Danger
                        });
                        return Redirect(Urls.GenerateUrl(Urls.UrlType.TopicCreate));
                    }

                    // Not using automapper for this one only, as a topic is a post and topic in one
                    category = CategoryService.Get(topicViewModel.Category);

                    // First check this user is allowed to create topics in this category
                    var permissions = PermissionService.GetPermissions(category, _membersGroup, MemberService, CategoryPermissionService);

                    // Check this users role has permission to create a post
                    if (permissions[AppConstants.PermissionDenyAccess].IsTicked || permissions[AppConstants.PermissionReadOnly].IsTicked || !permissions[AppConstants.PermissionCreateTopics].IsTicked)
                    {
                        // Throw exception so Ajax caller picks it up
                        ModelState.AddModelError(string.Empty, Lang("Errors.NoPermission"));
                    }
                    else
                    {
                        // We get the banned words here and pass them in, so its just one call
                        // instead of calling it several times and each call getting all the words back


                        topic = new Topic
                        {
                            Name = BannedWordService.SanitiseBannedWords(topicViewModel.TopicName, Dialogue.Settings().BannedWords),
                            Category = category,
                            CategoryId = category.Id,
                            Member = CurrentMember,
                            MemberId = CurrentMember.Id
                        };

                        // See if the user has actually added some content to the topic
                        if (!string.IsNullOrEmpty(topicViewModel.TopicContent))
                        {
                            // Check for any banned words
                            topicViewModel.TopicContent = BannedWordService.SanitiseBannedWords(topicViewModel.TopicContent, Dialogue.Settings().BannedWords);

                            // See if this is a poll and add it to the topic
                            if (topicViewModel.PollAnswers != null && topicViewModel.PollAnswers.Any(x => !string.IsNullOrEmpty(x.Answer)))
                            {
                                // Do they have permission to create a new poll
                                if (permissions[AppConstants.PermissionCreatePolls].IsTicked)
                                {
                                    // Create a new Poll
                                    var newPoll = new Poll
                                    {
                                        Member = CurrentMember,
                                        MemberId = CurrentMember.Id
                                    };

                                    // Create the poll
                                    PollService.Add(newPoll);

                                    // Save the poll in the context so we can add answers
                                    unitOfWork.SaveChanges();

                                    // Now sort the answers
                                    var newPollAnswers = new List<PollAnswer>();
                                    foreach (var pollAnswer in topicViewModel.PollAnswers)
                                    {
                                        // Attach newly created poll to each answer
                                        pollAnswer.Poll = newPoll;
                                        PollService.Add(pollAnswer);
                                        newPollAnswers.Add(pollAnswer);
                                    }
                                    // Attach answers to poll
                                    newPoll.PollAnswers = newPollAnswers;

                                    // Save the new answers in the context
                                    unitOfWork.SaveChanges();

                                    // Add the poll to the topic
                                    topic.Poll = newPoll;
                                }
                                else
                                {
                                    //No permission to create a Poll so show a message but create the topic
                                    ShowMessage(new GenericMessageViewModel
                                    {
                                        Message = Lang("Errors.NoPermissionPolls"),
                                        MessageType = GenericMessages.Info
                                    });
                                }
                            }

                            // Check for moderation
                            if (category.ModerateAllTopicsInThisCategory)
                            {
                                topic.Pending = true;
                                moderate = true;
                            }


                            // Create the topic
                            topic = TopicService.Add(topic);

                            // Save the changes
                            unitOfWork.SaveChanges();

                            // Now create and add the post to the topic
                            TopicService.AddLastPost(topic, topicViewModel.TopicContent, PostService);

                            // Update the users points score for posting
                            MemberPointsService.Add(new MemberPoints
                            {
                                Points = Settings.PointsAddedPerNewPost,
                                Member = CurrentMember,
                                MemberId = CurrentMember.Id,
                                RelatedPostId = topic.LastPost.Id
                            });

                            // Now check its not spam
                            var akismetHelper = new AkismetHelper();
                            if (akismetHelper.IsSpam(topic))
                            {
                                // Could be spam, mark as pending
                                topic.Pending = true;
                            }

                            // Subscribe the user to the topic as they have checked the checkbox
                            if (topicViewModel.SubscribeToTopic)
                            {
                                // Create the notification
                                var topicNotification = new TopicNotification
                                {
                                    Topic = topic,
                                    Member = CurrentMember,
                                    MemberId = CurrentMember.Id
                                };
                                //save
                                TopicNotificationService.Add(topicNotification);
                            }

                            try
                            {
                                unitOfWork.Commit();
                                if (!moderate)
                                {
                                    successfullyCreated = true;
                                }

                                // Update the users post count
                                MemberService.AddPostCount(CurrentMember);

                            }
                            catch (Exception ex)
                            {
                                unitOfWork.Rollback();
                                LogError(ex);
                                ModelState.AddModelError(string.Empty, Lang("Errors.GenericMessage"));
                            }
                        }
                        else
                        {
                            ModelState.AddModelError(string.Empty, Lang("Errors.GenericMessage"));
                        }
                    }
                }

                using (UnitOfWorkManager.NewUnitOfWork())
                {
                    if (successfullyCreated)
                    {
                        // Success so now send the emails
                        NotifyNewTopics(category);

                        // Redirect to the newly created topic
                        return Redirect($"{topic.Url}?postbadges=true");
                    }
                    if (moderate)
                    {
                        // Moderation needed
                        // Tell the user the topic is awaiting moderation
                        return MessageToHomePage(Lang("Moderate.AwaitingModeration"));
                    }
                }
            }

            ShowMessage();
            return Redirect(Urls.GenerateUrl(Urls.UrlType.TopicCreate));
        }

        private void NotifyNewTopics(Category cat)
        {

            // *CHANGE THIS TO BE CALLED LIKE THE BADGES VIA AN AJAX Method* 
            // TODO: This really needs to be an async call so it doesn't hang when a user creates  
            //  a topic if there are 1000's of users

            // Get all notifications for this category
            var notifications = CategoryNotificationService.GetByCategory(cat).Select(x => x.MemberId).ToList();

            if (notifications.Any())
            {
                // remove the current user from the notification, don't want to notify yourself that you 
                // have just made a topic!
                notifications.Remove(CurrentMember.Id);

                if (notifications.Count > 0)
                {
                    // Now get all the users that need notifying
                    var usersToNotify = MemberService.GetUsersById(notifications);

                    // Create the email
                    var sb = new StringBuilder();
                    sb.AppendFormat("<p>{0}</p>", string.Format(Lang("Topic.Notification.NewTopics"), cat.Name));
                    sb.AppendFormat("<p>{0}</p>", string.Concat(Settings.ForumRootUrlWithDomain, cat.Url));

                    // create the emails and only send them to people who have not had notifications disabled
                    var emails = usersToNotify.Where(x => x.DisableEmailNotifications != true).Select(user => new Email
                    {
                        Body = EmailService.EmailTemplate(user.UserName, sb.ToString()),
                        EmailFrom = Settings.NotificationReplyEmailAddress,
                        EmailTo = user.Email,
                        NameTo = user.UserName,
                        Subject = string.Concat(Lang("Topic.Notification.Subject"), Settings.ForumName)
                    }).ToList();

                    // and now pass the emails in to be sent
                    EmailService.SendMail(emails);
                }
            }
        }


        public PartialViewResult CreateTopicButton()
        {
            var viewModel = new CreateTopicButtonViewModel
            {
                LoggedOnUser = CurrentMember,
                CategoryId = 0
            };

            if (CurrentMember != null)
            {
                // Add all categories to a permission set
                var allCategories = CategoryService.GetAll();
                using (UnitOfWorkManager.NewUnitOfWork())
                {
                    foreach (var category in allCategories)
                    {
                        // Now check to see if they have access to any categories
                        // if so, check they are allowed to create topics - If no to either set to false
                        viewModel.UserCanPostTopics = false;
                        var permissionSet = PermissionService.GetPermissions(category, _membersGroup, MemberService, CategoryPermissionService);
                        if (permissionSet[AppConstants.PermissionCreateTopics].IsTicked)
                        {
                            viewModel.UserCanPostTopics = true;
                            break;
                        }
                    }

                    // Now check current page
                    if (AppHelpers.CurrentPage().DocumentTypeAlias == DialogueConfiguration.Instance.DocTypeForumCategory)
                    {
                        // In a category - So pass id to create button
                        viewModel.CategoryId = CurrentPage.Id;
                    }
                }
            }
            return PartialView(PathHelper.GetThemePartialViewPath("CreateTopicButton"), viewModel);
        }

    }
}