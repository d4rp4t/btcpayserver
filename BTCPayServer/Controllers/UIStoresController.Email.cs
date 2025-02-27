using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Models;
using BTCPayServer.Services.Mails;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MimeKit;

namespace BTCPayServer.Controllers;

public partial class UIStoresController
{
    [HttpGet("{storeId}/emails")]
    public async Task<IActionResult> StoreEmails(string storeId)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var configured = await _emailSenderFactory.IsComplete(store.Id);
        if (!configured && !TempData.HasStatusMessage())
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Warning,
                Html = "You need to configure email settings before this feature works." +
                          $" <a class='alert-link' href='{Url.Action("StoreEmailSettings", new { storeId })}'>Configure store email settings</a>."
            });
        }

        var vm = new StoreEmailRuleViewModel { Rules = store.GetStoreBlob().EmailRules ?? [] };
        return View(vm);
    }

    [HttpPost("{storeId}/emails")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> StoreEmails(string storeId, StoreEmailRuleViewModel vm, string command)
    {
        vm.Rules ??= [];
        int commandIndex = 0;
            
        var indSep = command.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (indSep.Length > 1)
        {
            commandIndex = int.Parse(indSep[1], CultureInfo.InvariantCulture);
        }

        if (command.StartsWith("remove", StringComparison.InvariantCultureIgnoreCase))
        {
            vm.Rules.RemoveAt(commandIndex);
        }
        else if (command == "add")
        {
            vm.Rules.Add(new StoreEmailRule());

            return View(vm);
        }

        for (var i = 0; i < vm.Rules.Count; i++)
        {
            var rule = vm.Rules[i];

            if (!string.IsNullOrEmpty(rule.To) && rule.To.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Any(s => !MailboxAddressValidator.TryParse(s, out _)))
            {
                ModelState.AddModelError($"{nameof(vm.Rules)}[{i}].{nameof(rule.To)}",
                    StringLocalizer["Invalid mailbox address provided. Valid formats are: '{0}' or '{1}'", "test@example.com", "Firstname Lastname <test@example.com>"]);
            }
            else if (!rule.CustomerEmail && string.IsNullOrEmpty(rule.To))
                ModelState.AddModelError($"{nameof(vm.Rules)}[{i}].{nameof(rule.To)}",
                    StringLocalizer["Either recipient or \"Send the email to the buyer\" is required"]);
        }

        if (!ModelState.IsValid)
        {
            return View(vm);
        }

        var store = HttpContext.GetStoreData();

        if (store == null)
            return NotFound();

        string message = "";

        // update rules
        var blob = store.GetStoreBlob();
        blob.EmailRules = vm.Rules;
        if (store.SetStoreBlob(blob))
        {
            await _storeRepo.UpdateStore(store);
            message += StringLocalizer["Store email rules saved."] + " ";
        }

        if (command.StartsWith("test", StringComparison.InvariantCultureIgnoreCase))
        {
            try
            {
                var rule = vm.Rules[commandIndex];
                if (await _emailSenderFactory.IsComplete(store.Id))
                {
                    var recipients = rule.To.Split(",", StringSplitOptions.RemoveEmptyEntries)
                        .Select(o =>
                        {
                            MailboxAddressValidator.TryParse(o, out var mb);
                            return mb;
                        })
                        .Where(o => o != null)
                        .ToArray();

                    var emailSender = await _emailSenderFactory.GetEmailSender(store.Id);
                    emailSender.SendEmail(recipients.ToArray(), null, null, $"[TEST] {rule.Subject}", rule.Body);
                    message += StringLocalizer["Test email sent — please verify you received it."];
                }
                else
                {
                    message += StringLocalizer["Complete the email setup to send test emails."];
                }
            }
            catch (Exception ex)
            {
                TempData[WellKnownTempData.ErrorMessage] = message + StringLocalizer["Error sending test email: {0}", ex.Message].Value;
                return RedirectToAction("StoreEmails", new { storeId });
            }
        }

        if (!string.IsNullOrEmpty(message))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Success,
                Message = message
            });
        }

        return RedirectToAction("StoreEmails", new { storeId });
    }

    public class StoreEmailRuleViewModel
    {
        public List<StoreEmailRule> Rules { get; set; }
    }

    public class StoreEmailRule
    {
        [Required]
        public string Trigger { get; set; }
            
        public bool CustomerEmail { get; set; }
            
           
        public string To { get; set; }
            
        [Required]
        public string Subject { get; set; }
            
        [Required]
        public string Body { get; set; }
    }

    [HttpGet("{storeId}/email-settings")]
    public async Task<IActionResult> StoreEmailSettings(string storeId)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var emailSender = await _emailSenderFactory.GetEmailSender(store.Id);
        var data = await emailSender.GetEmailSettings() ?? new EmailSettings();
        var fallbackSettings = emailSender is StoreEmailSender { FallbackSender: { } fallbackSender }
            ? await fallbackSender.GetEmailSettings()
            : null;
        var settings = data != fallbackSettings ? data : new EmailSettings();
        return View(new EmailsViewModel(settings, fallbackSettings));
    }

    [HttpPost("{storeId}/email-settings")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> StoreEmailSettings(string storeId, EmailsViewModel model, string command, [FromForm] bool useCustomSMTP = false)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        model.FallbackSettings = await _emailSenderFactory.GetEmailSender(store.Id) is StoreEmailSender { FallbackSender: not null } storeSender
            ? await storeSender.FallbackSender.GetEmailSettings()
            : null;
        if (model.FallbackSettings is null) useCustomSMTP = true;
        ViewBag.UseCustomSMTP = useCustomSMTP;
        if (useCustomSMTP)
        {
            model.Settings.Validate("Settings.", ModelState);
        }
        if (command == "Test")
        {
            try
            {
                if (useCustomSMTP)
                {
                    if (model.PasswordSet)
                    {
                        model.Settings.Password = store.GetStoreBlob().EmailSettings.Password;
                    }
                }
                    
                if (string.IsNullOrEmpty(model.TestEmail))
                    ModelState.AddModelError(nameof(model.TestEmail), new RequiredAttribute().FormatErrorMessage(nameof(model.TestEmail)));
                if (!ModelState.IsValid)
                    return View(model);
                var settings = useCustomSMTP ? model.Settings : model.FallbackSettings;
                using var client = await settings.CreateSmtpClient();
                var message = settings.CreateMailMessage(MailboxAddress.Parse(model.TestEmail), $"{store.StoreName}: Email test", StringLocalizer["You received it, the BTCPay Server SMTP settings work."], false);
                await client.SendAsync(message);
                await client.DisconnectAsync(true);
                TempData[WellKnownTempData.SuccessMessage] = StringLocalizer["Email sent to {0}. Please verify you received it.", model.TestEmail].Value;
            }
            catch (Exception ex)
            {
                TempData[WellKnownTempData.ErrorMessage] = StringLocalizer["Error: {0}", ex.Message].Value;
            }
            return View(model);
        }
        if (command == "ResetPassword")
        {
            var storeBlob = store.GetStoreBlob();
            storeBlob.EmailSettings.Password = null;
            store.SetStoreBlob(storeBlob);
            await _storeRepo.UpdateStore(store);
            TempData[WellKnownTempData.SuccessMessage] = StringLocalizer["Email server password reset"].Value;
        }
        var unsetCustomSMTP = !useCustomSMTP && store.GetStoreBlob().EmailSettings is not null;
        if (useCustomSMTP || unsetCustomSMTP)
        {
            if (model.Settings.From is not null && !MailboxAddressValidator.IsMailboxAddress(model.Settings.From))
            {
                ModelState.AddModelError("Settings.From", StringLocalizer["Invalid email"]);
            }
            if (!ModelState.IsValid)
                return View(model);
            var storeBlob = store.GetStoreBlob();
            if (storeBlob.EmailSettings != null && new EmailsViewModel(storeBlob.EmailSettings, model.FallbackSettings).PasswordSet)
            {
                model.Settings.Password = storeBlob.EmailSettings.Password;
            }
            storeBlob.EmailSettings = unsetCustomSMTP ? null : model.Settings;
            store.SetStoreBlob(storeBlob);
            await _storeRepo.UpdateStore(store);
            TempData[WellKnownTempData.SuccessMessage] = StringLocalizer["Email settings modified"].Value;
        }
        return RedirectToAction(nameof(StoreEmailSettings), new { storeId });
    }
}
