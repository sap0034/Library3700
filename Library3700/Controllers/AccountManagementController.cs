﻿using Library3700.Models.Objects.Account;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using System.Security.Claims;
using Library3700.Models;
using System.Security.Cryptography;
using System.Text;

namespace Library3700.Controllers
{
    public class AccountManagementController : Controller
    {
        NotificationController notification = new NotificationController();
        public class RegisterRequest
        {
            public string FirstName { get; set; }

            public string LastName { get; set; }

            public string EmailAddress { get; set; }

            public bool IsLibrarian { get; set; }
        }
        
        public class ChangeAccountStatusRequest
        {
            public string User { get; set; }
            public byte StatusId { get; set; }
        }

        /// <summary>
        /// If user tries to hit index, redirect to Home action
        /// </summary>
        /// <returns></returns>
        public ActionResult Index()
        {
            return RedirectToAction("Home");
        }

        /// <summary>
        /// Display the appropriate home page based on who is logged in.
        /// </summary>
        /// <returns></returns>
        public ActionResult Home()
        {
            ClaimsIdentity identity = (ClaimsIdentity)User.Identity;
            IEnumerable<Claim> claims = identity.Claims;

            SetActiveAccount();

            if (User.IsInRole("librarian"))
            {
                return View("LibrarianHome");
            }
            else if (User.IsInRole("patron"))
            {
                return View("PatronHome");
            }
            else
            {
                return RedirectToAction("LogOut", "Login");
            }
        }
        
        /// <summary>
        /// Display add account page.
        /// </summary>
        /// <returns></returns>
        public ActionResult AddAccount()
        {
            return View();
        }

        /// <summary>
        /// Handle form request to register new user.
        /// </summary>
        /// <param name="request">Form data</param>
        /// <returns></returns>
        public ActionResult RegisterNewAccount(RegisterRequest request)
        {
            try
            {
                using (var db = new LibraryEntities())
                {
                    if (db.Logins.Where(x => x.Username == request.EmailAddress).Any())
                    {
                        throw new ApplicationException(
                            "A user with the email address " + request.EmailAddress + " already exists!");
                    }

                    var newAccount = new Account
                    {
                        FirstName = request.FirstName,
                        LastName = request.LastName,
                        IsLibrarian = request.IsLibrarian
                    };

                    string temporaryPassword = GenerateTemporaryPassword();
                    string temporaryPasswordHash = HashString(temporaryPassword);

                    using (var transaction = db.Database.BeginTransaction())
                    {
                        try
                        {
                            db.Accounts.Add(newAccount);
                            db.SaveChanges();

                            var newLogin = new Login
                            {
                                AccountId = newAccount.AccountId,
                                Username = request.EmailAddress,
                                PasswordHash = temporaryPasswordHash,
                                IsPasswordTemporary = true
                            };

                            db.Logins.Add(newLogin);

                            var accountStatus = new AccountStatusLog
                            {
                                AccountId = newAccount.AccountId,
                                AccountStatusTypeId = 1,
                                LogDateTime = DateTime.Now
                            };

                            db.AccountStatusLogs.Add(accountStatus);

                            db.SaveChanges();
                            transaction.Commit();
                        }
                        catch (Exception e)
                        {
                            transaction.Rollback();
                            throw e;
                        }
                    }

                    return notification.AddAccountSuccess(temporaryPassword);
                }
            }
            catch
            {
                return notification.AddAccountFailure();
            }
        }

        /// <summary>
        /// Displays page to change user's temporary password.
        /// </summary>
        /// <returns></returns>
        public ActionResult SetPasswordConfirm()
        {
            SetActiveAccount();  // establish identity since we have not yet visited the homepage
            return View();
        }

        /// <summary>
        /// Handles form request for setting new password.
        /// </summary>
        /// <param name="passwordHash">Hash of new password to set</param>
        /// <returns></returns>
        [HttpPost]
        public ActionResult UpdatePassword(string passwordHash)
        {
            using (var db = new LibraryEntities())
            {
                using (var transaction = db.Database.BeginTransaction())
                {
                    try
                    {
                        var activeAccount = (AccountAdapter)System.Web.HttpContext.Current.Session["activeAccount"];

                        Login userLogin = db.Logins.Where(x => x.AccountId == activeAccount.AccountNumber).SingleOrDefault();
                        userLogin.PasswordHash = passwordHash;
                        userLogin.IsPasswordTemporary = false;
                        db.SaveChanges();
                        transaction.Commit();
                    }
                    catch
                    {
                        transaction.Rollback();
                        return new HttpStatusCodeResult(System.Net.HttpStatusCode.InternalServerError);
                    }
                }           
            }

            return RedirectToAction("Home");
        }

        /// <summary>
        /// Displays page for a librarian to assign a user a new temporary password.
        /// </summary>
        /// <returns>Reset password view</returns>
        public ActionResult ResetPassword()
        {
            return View();
        }

        /// <summary>
        /// Handle request to assign user a new temporary password
        /// </summary>
        /// <param name="user">User to give new temporary password</param>
        /// <returns>Json result indicating success and new temporary password.</returns>
        [HttpPost]
        public ActionResult NewTemporaryPassword(string user)
        {
            using (var db = new LibraryEntities())
            {
                using (var transaction = db.Database.BeginTransaction())
                {
                    try
                    {
                        Login targetLogin = db.Logins.Where(x => x.Username == user).SingleOrDefault();
                        if (targetLogin == null)
                        {
                            return notification.ResetPasswordUserNotFound();
                        }

                        var newTempPassword = GenerateTemporaryPassword();

                        targetLogin.PasswordHash = HashString(newTempPassword);
                        targetLogin.IsPasswordTemporary = true;

                        db.SaveChanges();
                        transaction.Commit();

                        return notification.ResetPasswordSuccess(newTempPassword);
                    }
                    catch
                    {
                        transaction.Rollback();
                        return notification.UnknownError();
                    }
                }
            }
        }

        /// <summary>
        /// Display view to update a user's account status
        /// </summary>
        /// <returns></returns>
        public ActionResult UpdateAccountStatus()
        {
            using (var db = new LibraryEntities())
            {
                var statuses = db.AccountStatusTypes.ToList();
                return View(statuses);
            }
        }

        /// <summary>
        /// Handle update account status form request
        /// </summary>
        /// <param name="request">Request parameters</param>
        /// <returns></returns>
        [HttpPost]
        public ActionResult ChangeAccountStatus(ChangeAccountStatusRequest request)
        {
            using (var db = new LibraryEntities())
            {
                // select account associated with username
                var targetAccount = FindAccount(db, request.User);

                if (targetAccount == null)
                {
                    // if user not found
                    return notification.ResetPasswordUserNotFound();
                }

                var newStatus = new AccountStatusLog
                {
                    AccountId = targetAccount.AccountId,
                    AccountStatusTypeId = request.StatusId,
                    LogDateTime = DateTime.Now
                };

                using (var transaction = db.Database.BeginTransaction())
                {
                    try
                    {
                        db.AccountStatusLogs.Add(newStatus);
                        db.SaveChanges();
                        transaction.Commit();

                        return notification.UpdateAccountStatusSuccess();
                    }
                    catch (Exception)
                    {
                        transaction.Rollback();
                        return notification.UnknownError();
                    }
                }
            }
        }

        /// <summary>
        /// Find an account with a given ID
        /// </summary>
        /// <param name="db">Database context</param>
        /// <param name="accountId">Accoutn ID/number</param>
        /// <returns></returns>
        private Account FindAccount(LibraryEntities db, int accountId)
        {
            return db.Accounts.Where(x => x.AccountId == accountId).SingleOrDefault();
        }

        /// <summary>
        /// Find an account with a given username
        /// </summary>
        /// <param name="db">Database context</param>
        /// <param name="username">Associated username</param>
        /// <returns></returns>
        private Account FindAccount(LibraryEntities db, string username)
        {
            int accountId = db.Logins.Where(x => x.Username == username).Select(x => x.AccountId).SingleOrDefault();
            return FindAccount(db, accountId);
        }

        /// <summary>
        /// Gets current status for an account
        /// </summary>
        /// <param name="accountId">Account number/ID</param>
        /// <returns>Current account status</returns>
        private AccountStatusType AccountStatus(LibraryEntities db, int accountId)
        {
            var recentLog = db.AccountStatusLogs
                    .Where(x => x.AccountId == accountId)
                    .OrderByDescending(x => x.LogDateTime)
                    .FirstOrDefault();

            var currentStatus = db.AccountStatusTypes
                .Where(x => x.AccountStatusTypeId == recentLog.AccountStatusTypeId)
                .SingleOrDefault();

            return currentStatus;
        }

        /// <summary>
        /// Gets current status for an account
        /// </summary>
        /// <param name="db">Database context</param>
        /// <param name="username">Username for an associated account</param>
        /// <returns>Current account status</returns>
        public AccountStatusType AccountStatus(LibraryEntities db, string username)
        {
            int accountId = db.Logins.Where(x => x.Username == username).Select(x => x.AccountId).SingleOrDefault();
            return AccountStatus(db, accountId);
        }

        /// <summary>
        /// Sets active account for the controller
        /// </summary>
        private void SetActiveAccount()
        {
            ClaimsIdentity identity = (ClaimsIdentity)User.Identity;
            IEnumerable<Claim> claims = identity.Claims;

            if (User.IsInRole("librarian"))
            {
                System.Web.HttpContext.Current.Session["activeAccount"] = new Librarian
                {
                    LibrarianEmailAddress = claims.Where(x => x.Type == ClaimTypes.Email).Single().Value,
                    LibrarianFirstName = claims.Where(x => x.Type == ClaimTypes.GivenName).Single().Value,
                    LibrarianLastName = claims.Where(x => x.Type == ClaimTypes.Surname).Single().Value,
                    LibrarianId = Int32.Parse(claims.Where(x => x.Type == ClaimTypes.UserData).Single().Value)
                };
            }
            else if (User.IsInRole("patron"))
            {
                System.Web.HttpContext.Current.Session["activeAccount"] = new Patron
                {
                    PatronEmailAddress = claims.Where(x => x.Type == ClaimTypes.Email).Single().Value,
                    PatronFirstName = claims.Where(x => x.Type == ClaimTypes.GivenName).Single().Value,
                    PatronLastName = claims.Where(x => x.Type == ClaimTypes.Surname).Single().Value,
                    PatronId = Int32.Parse(claims.Where(x => x.Type == ClaimTypes.UserData).Single().Value)
                };
            }
        }

        /// <summary>
        /// Hash a string with SHA512
        /// </summary>
        /// <param name="source">String to hash</param>
        /// <returns>SHA512 hash of string</returns>
        private static string HashString(string source)
        {
            using (var sha512 = SHA512.Create())
            {
                byte[] bytes = sha512.ComputeHash(Encoding.UTF8.GetBytes(source));

                StringBuilder sb = new StringBuilder();
                foreach (var b in bytes)
                {
                    sb.Append(b.ToString("x2"));
                }

                return sb.ToString();
            }
        }

        /// <summary>
        /// Generate a random 8 character alphanumeric password
        /// </summary>
        /// <returns>Password</returns>
        private static string GenerateTemporaryPassword()
        {
            Random random = new Random();
            const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
            return new String(Enumerable.Repeat(chars, 8).Select(s => s[random.Next(s.Length)]).ToArray());
        }
    }
}