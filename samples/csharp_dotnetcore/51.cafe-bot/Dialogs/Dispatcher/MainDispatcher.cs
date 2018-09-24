﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.BotBuilderSamples
{
    /// <summary>
    /// The <see cref="MainDialog"/> is first dialog that runs after a user begins a conversation.
    /// </summary>
    /// <remarks>
    /// The <see cref="MainDialog"/> responsibility is to:
    /// - Start message.
    ///   Display the inital message the user sees when they begin a conversation.
    /// - Help.
    ///   Provide the user about the commands the bot can process.
    /// - Start other dialogs to perform more complex operations.
    ///   Begin the <see cref="GreetingDialog"/> if the user greets the bot, which will
    ///   prompt the user for name and city.
    /// </remarks>
    public class MainDispatcher : ComponentDialog
    {
        // Supported LUIS Main Dispatcher Intents
        public const string NoneIntent = "None";
        public const string CancelIntent = "Cancel";

        // Conversation state properties
        public const string UserProfileProperty = "userProfile";
        public const string MainDispatcherStateProperty = "mainDispatcherState";
        public const string ReservationProperty = "reservationProperty";

        // When user responds to what can you do card, a query property is set in response.
        private const string QUERY_PROPERTY = "query";

        /// <summary>
        /// Key in the bot config (.bot file) for the LUIS instance.
        /// In the .bot file, multiple instances of LUIS can be configured.
        /// </summary>
        public static readonly string LuisKey = "BasicBotLUIS";

        private readonly BotServices _services;
        private readonly ILogger _logger;
        private readonly IStatePropertyAccessor<OnTurnProperty> _onTurnAccessor;
        private readonly IStatePropertyAccessor<UserProfile> _userProfileAccessor;
        private readonly IStatePropertyAccessor<DialogState> _mainDispatcherAccessor;
        private readonly IStatePropertyAccessor<ReservationProperty> _reservationAccessor;

        private readonly DialogSet _dialogs;

        public MainDispatcher(BotServices services, IStatePropertyAccessor<OnTurnProperty> onTurnAccessor, UserState userState, ConversationState conversationState, ILoggerFactory loggerFactory)
                    : base(nameof(MainDispatcher))
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _onTurnAccessor = onTurnAccessor ?? throw new ArgumentNullException(nameof(onTurnAccessor));
            if (conversationState == null)
            {
                throw new ArgumentNullException(nameof(conversationState));
            }
            if (userState == null)
            {
                throw new ArgumentNullException(nameof(userState));
            }

            // Create logger for this class.
            _logger = loggerFactory.CreateLogger<MainDispatcher>();

            // Create state objects for user, conversation and dialog states.
            _userProfileAccessor = conversationState.CreateProperty<UserProfile>(UserProfileProperty);
            _mainDispatcherAccessor = conversationState.CreateProperty<DialogState>(MainDispatcherStateProperty);
            _reservationAccessor = conversationState.CreateProperty<ReservationProperty>(ReservationProperty);

            // add dialogs
            _dialogs = new DialogSet(_mainDispatcherAccessor);
            AddDialog(new WhatCanYouDo());
            AddDialog(new QnADialog(services, _userProfileAccessor));
            AddDialog(new WhoAreYouDialog(services, conversationState, _userProfileAccessor, onTurnAccessor, _reservationAccessor));
            AddDialog(new BookTableDialog(services, _reservationAccessor, onTurnAccessor, _userProfileAccessor, conversationState));
        }

        protected override async Task<DialogTurnResult> OnBeginDialogAsync(DialogContext innerDc, object options, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await MainDispatchAsync(innerDc);
        }

        protected override async Task<DialogTurnResult> OnContinueDialogAsync(DialogContext innerDc, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await MainDispatchAsync(innerDc);
        }

        /**
          * Main Dispatch 
          * 
          * This method examines the incoming turn property to determine  
          * 1. If the requested operation is permissible - e.g. if user is in middle of a dialog, 
          *     then an out of order reply should not be allowed.
          * 2. Calls any outstanding dialogs to continue
          * 3. If results is no-match from outstanding dialog .OR. if there are no outstanding dialogs,
          *    decide which child dialog should begin and start it
          * 
          * @param {Object} dc dialog context
          */
        protected async Task<DialogTurnResult> MainDispatchAsync(DialogContext innerDc)
        {
            var context = innerDc.Context;
            // get on turn property through the property accessor
            var onTurnProperty = await _onTurnAccessor.GetAsync(context).ConfigureAwait(false);

            // Evaluate if the requested operation is possible/ allowed.
            var reqOpStatus = await IsRequestedOperationPossible(innerDc.ActiveDialog.Id, onTurnProperty.Intent);
            if (!reqOpStatus.allowed)
            {
                await context.SendActivityAsync(reqOpStatus.reason);
                // Nothing to do here. End main dialog.
                return await innerDc.EndDialogAsync();
            }

            // continue outstanding dialogs
            var dialogTurnResult = await innerDc.ContinueDialogAsync();

            // This will only be empty if there is no active dialog in the stack.
            // Removing check for dialogTurnStatus here will break successful cancellation of child dialogs. 
            // E.g. who are you -> cancel -> yes flow.
            if (!context.Responded && dialogTurnResult != null && dialogTurnResult.Status != DialogTurnStatus.Complete)
            {
                // No one has responded so start the right child dialog.
                dialogTurnResult = await this.BeginDialogAsync(innerDc, onTurnProperty);
            }

            if (dialogTurnResult == null) return await innerDc.EndDialogAsync();

            // Examine result from dc.continue() or from the call to beginChildDialog().
            switch (dialogTurnResult.Status)
            {
                case DialogTurnStatus.Complete:
                    // The active dialog finished successfully. Ask user if they need help with anything else.
                    await context.SendActivityAsync(MessageFactory.SuggestedActions(Helpers.GenSuggestedQueries(), "Is there anything else I can help you with ?"));
                    break;

                case DialogTurnStatus.Waiting:
                    // The active dialog is waiting for a response from the user, so do nothing
                    break;

                case DialogTurnStatus.Cancelled:
                    // The active dialog"s stack has been cancelled
                    await innerDc.CancelAllDialogsAsync();
                    break;
            }

            return dialogTurnResult;
        }

        /**
         * Method to evaluate if the requested user operation is possible.
         * User could be in the middle of a multi-turn dialog where interruption might not be possible or allowed.
         * 
         * @param {String} activeDialog
         * @param {String} requestedOperation 
         * @returns {Object} outcome object
         */
        protected async Task<(bool allowed, string reason)> IsRequestedOperationPossible(string activeDialog, string requestedOperation)
        {
            (bool allowed, string reason) outcome = (true, string.Empty);

            // E.g. What_can_you_do is not possible when you are in the middle of Who_are_you dialog
            if (requestedOperation.Equals(nameof(WhatCanYouDo)))
            {
                if (activeDialog.Equals(nameof(WhatCanYouDo)))
                {
                    outcome.allowed = false;
                    outcome.reason = "Sorry! I'm unable to process that. You can say 'cancel' to cancel this conversation..";
                }
            }
            else if (requestedOperation.Equals(CancelIntent))
            {
                if (string.IsNullOrWhiteSpace(activeDialog))
                {
                    outcome.allowed = false;
                    outcome.reason = "Sure, but there is nothing to cancel..";
                }
            }

            return outcome;
        }

        /**
         * Helper method to begin what can you do dialog.
         *
         * @param {Object} dc dialog context
         * @param {Object} onTurnProperty
         */
        private async Task<DialogTurnResult> BeginWhatCanYouDoDialog(DialogContext innerDc, OnTurnProperty onTurnProperty)
        {
            var context = innerDc.Context;

            // Handle case when user interacted with what can you do card.
            // What can you do card sends a custom data property with intent name, text value and possible entities.
            // See ../WhatCanYouDo/Resources/whatCanYouDoCard.json for card definition.
            var queryProperty = (onTurnProperty.Entities ?? new List<EntityProperty>()).Where(item => string.Compare(item.EntityName, QUERY_PROPERTY) == 0);
            if (queryProperty.Count() > 0)
            {
                Dictionary<string, string> response;
                try
                {
                    response = JsonConvert.DeserializeObject<Dictionary<string, string>>(queryProperty.ElementAtOrDefault(0).Value as string);
                }
                catch (Exception ex)
                {
                    await context.SendActivityAsync("Choose a query from the card drop down before you click `Let's talk!`");
                    return new DialogTurnResult(DialogTurnStatus.Empty, null);
                }

                if (response.TryGetValue("text", out var text))
                {
                    context.Activity.Text = text;
                    await context.SendActivityAsync($"You said: '{context.Activity.Text}'.");
                }

                // Create a set a new onturn property
                await _onTurnAccessor.SetAsync(context, OnTurnProperty.FromCardInput(response));
                return await BeginDialogAsync(innerDc, response);
            }

            return await innerDc.BeginDialogAsync(nameof(WhatCanYouDo));
        }
    }
}
