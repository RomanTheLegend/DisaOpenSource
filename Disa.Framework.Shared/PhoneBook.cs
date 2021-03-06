using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using ProtoBuf;

namespace Disa.Framework
{
    public static class PhoneBook
    {
        public static string Mnc { get; set; }
        public static string Mcc { get; set; }
        public static string Country { get; set; }
        public static string Language { get; set; }

        private static readonly object PhoneBookContactsLock = new object();
        private static List<PhoneBookContact> _phoneBookContacts;
        public static List<PhoneBookContact> PhoneBookContacts
        {
            get 
            { 
                lock (PhoneBookContactsLock)
                {
                    if (_phoneBookContacts == null)
                    {
                        var phoneBookContacts = GetCachedPhoneBooksContacts();
                        if (phoneBookContacts == null)
                        {
                            phoneBookContacts = Platform.GetPhoneBookContacts();
                            SaveCachedPhoneBookContacts(phoneBookContacts);
                        }
                        _phoneBookContacts = phoneBookContacts;
                        OnPhoneContactsUpdated();
                    }
                    return _phoneBookContacts;
                }
            }
        }

        private static bool _isUpdating;

        private static Action _refreshMncMcc;

        static PhoneBook()
        {
            Mnc = "000";
            Mcc = "000";
            Country = "US";
            Language = "en";
        }

        private static readonly object _contactsCacheLock = new object();

        private static string GetPhoneBookContactsCachePath()
        {
            var settingsPath = Platform.GetSettingsPath();
            var phoneBooksContactsCache = Path.Combine(settingsPath, "PhoneBookContactsCache.db");

            return phoneBooksContactsCache;
        }

        private static List<PhoneBookContact> GetCachedPhoneBooksContacts()
        {
            lock (_contactsCacheLock)
            {
                var path = GetPhoneBookContactsCachePath();
                try
                {
                    if (!File.Exists(path))
                    {
                        return null;
                    }
                    using (var fs = File.OpenRead(path))
                    {
                        return Serializer.Deserialize<List<PhoneBookContact>>(fs);
                    }
                }
                catch (Exception ex)
                {
                    Utils.DebugPrint("Failed to get cached phonebook contacts (nuking): " + ex);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                return null;
            }
        }
            
        private static void SaveCachedPhoneBookContacts(List<PhoneBookContact> phoneBookContacts)
        {
            lock (_contactsCacheLock)
            {
                var path = GetPhoneBookContactsCachePath();
                if (phoneBookContacts == null)
                {
                    Utils.DebugPrint("Nuking cached phone book contacts as we're trying to save nothing.");
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    return;
                }
                try
                {
                    using (var fs = File.Create(path))
                    {
                        Serializer.Serialize(fs, phoneBookContacts);
                    }
                }
                catch (Exception ex)
                {
                    Utils.DebugPrint("Failed to save cached phonebook contacts (nuking): " + ex);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
            }
        }

        public static void RegisterMncMccRefresher(Action refreshMncMcc)
        {
            _refreshMncMcc = refreshMncMcc;
        }

        public static void RefreshMncMcc()
        {
            if (_refreshMncMcc != null)
            {
                _refreshMncMcc();
            }
        }

        private class PhoneContactsUpdatingDisposable : IDisposable
        {
            public PhoneContactsUpdatingDisposable()
            {
                _isUpdating = true;
            }

            public void Dispose()
            {
                _isUpdating = false;
            }
        }

        public static string GetAreaCodeFromFromInternationalNumber(string internationalNumber, string localeCountry)
        {
            try
            {
                #if __ANDROID__
                var phoneUtil = Com.Google.I18n.Phonenumbers.PhoneNumberUtil.Instance;
                var parsedNumber = phoneUtil.ParseAndKeepRawInput(internationalNumber, localeCountry);
                String nationalSignificantNumber = phoneUtil.GetNationalSignificantNumber(parsedNumber);
                int nationalDestinationCodeLength = phoneUtil.GetLengthOfNationalDestinationCode(parsedNumber);

                if (nationalDestinationCodeLength > 0) 
                {
                    var nationalDestinationCode = nationalSignificantNumber.Substring(0, nationalDestinationCodeLength);
                    return nationalDestinationCode;
                }
                #else
                throw new NotImplementedException("Not implemented");
                #endif
            }
            catch (Exception ex)
            {
                Utils.DebugPrint("Could not get area code from international number; " + ex);
            }
            return null;
        }

        public static string GetAreaCodeFromFromInternationalNumber(string internationalNumber)
        {
            return GetAreaCodeFromFromInternationalNumber(internationalNumber, Country);
        }

        public static string GetCountryLocaleFromInternationalNumber(string internationalNumber)
        {
            try
            {
                #if __ANDROID__
                var phoneUtil = Com.Google.I18n.Phonenumbers.PhoneNumberUtil.Instance;
                var phoneNumber = phoneUtil.ParseAndKeepRawInput(internationalNumber, Country);
                if (phoneNumber == null)
                {
                    goto End;
                }
                var region = phoneUtil.GetRegionCodeForNumber(phoneNumber);
                if (string.IsNullOrWhiteSpace(region))
                {
                    goto End;
                }
                return region;
                #else
                throw new NotImplementedException("Not implemented");
                #endif

            }
            catch (Exception ex)
            {
                Utils.DebugPrint("Could not get country locale from international number. " + ex);
            }

            End:

            return Country;
        }

        public static bool TryConvertToInternationalNumber(string numberIn, out string numberOut, string localeCountry)
        {
            try
            {
                #if __ANDROID__
                var phoneUtil = Com.Google.I18n.Phonenumbers.PhoneNumberUtil.Instance;
                var phoneNumber = phoneUtil.ParseAndKeepRawInput(numberIn, localeCountry);
                if (phoneUtil.IsValidNumber(phoneNumber))
                {
                    numberOut = phoneUtil.Format(phoneNumber, 
                        Com.Google.I18n.Phonenumbers.PhoneNumberUtil.PhoneNumberFormat.International);
                    return true;
                }
                #endif
            }
            catch
            {
                // fall-through
            }

            numberOut = String.Copy(numberIn);
            return false;
        }

        public static bool TryConvertToInternationalNumber(string numberIn, out string numberOut)
        {
            return TryConvertToInternationalNumber(numberIn, out numberOut, Country);
        }

        public static string TryGetPhoneNumberLegible(string number, string localeCountry)
        {
            try
            {
                #if __ANDROID__
                var phoneUtil = Com.Google.I18n.Phonenumbers.PhoneNumberUtil.Instance;
                var phoneNumber = phoneUtil.ParseAndKeepRawInput(number, localeCountry);
                return phoneUtil.Format(phoneNumber, 
                    Com.Google.I18n.Phonenumbers.PhoneNumberUtil.PhoneNumberFormat.International);
                #else
                throw new NotImplementedException("Not implemented");
                #endif
            }
            catch (Exception ex)
            {
                Utils.DebugPrint("Could not get legible phone number. " + ex.Message);
            }

            return number;   
        }

        public static string TryGetPhoneNumberLegible(string number)
        {
            return TryGetPhoneNumberLegible(number, Country);
        }

        public static bool IsPossibleNumber(string address, string localeCountry)
        {
#if __ANDROID__
            return Com.Google.I18n.Phonenumbers.PhoneNumberUtil.Instance.IsPossibleNumber(address, localeCountry);
#else
            throw new NotImplementedException("Not implemented");
#endif
        }

        public static bool IsPossibleNumber(string address)
        {
            return IsPossibleNumber(address, Country);
        }

        public static Tuple<string, string> FormatPhoneNumber(string number, string localeCountry)
        {
            try
            {

#if __ANDROID__
                var phoneUtil = Com.Google.I18n.Phonenumbers.PhoneNumberUtil.Instance;
                var phoneNumber = phoneUtil.ParseAndKeepRawInput(number, localeCountry);
                if (phoneNumber == null)
                    return null;
                var cc = phoneNumber.CountryCode.ToString(CultureInfo.InvariantCulture);
                var n = phoneNumber.NationalNumber.ToString(CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(cc) || string.IsNullOrEmpty(n))
                    return null;
                if (phoneUtil.IsValidNumber(phoneNumber))
                {
                    return new Tuple<string, string>(phoneNumber.CountryCode.ToString(CultureInfo.InvariantCulture),
                                                        phoneNumber.NationalNumber.ToString(CultureInfo.InvariantCulture));
                }
#else
                throw new NotImplementedException("Not implemented");
#endif

            }
            catch (Exception ex)
            {
                Utils.DebugPrint("Could not format phone number. " + ex.Message);
            }

            return null;
        }

        public static Tuple<string, string> FormatPhoneNumber(string number)
        {
            return FormatPhoneNumber(number, Country);
        }

        public static Task ForceUpdate(Service service)
        {
            return ForceUpdate(new [] { service });
        }

        public static Task ForceUpdate(Service[] services)
        {
            return Task.Factory.StartNew(() =>
            {
                Utils.DebugPrint("Querying phone contacts...");
                _phoneBookContacts = Platform.GetPhoneBookContacts();
                if (services != null)
                {
                    foreach (var service in services)
                    {
                        if (service != null)
                        {
                            SyncService(service);
                        }
                    }
                }
            });
        }

        internal static void SyncService(Service service)
        {
            if (ServiceManager.IsRunning(service))
            {
                Utils.DebugPrint("Refreshing service contacts...");
                service.RefreshPhoneBookContacts();
            }
            else
            {
                Utils.DebugPrint("Force refreshing service " + service.Information.ServiceName +
                    " isn't possible. Why? It isn't running!");
            }
            Utils.DebugPrint("Finished refreshing service contacts. Calling contacts update and bubble group update.");
            BubbleGroupUpdater.Update(service);
        }

        public static void OnPhoneContactsUpdated()
        {
            Task.Factory.StartNew(() =>
            {
                if (_isUpdating)
                {
                    Utils.DebugPrint("Contacts are already updating... Returning...");
                    return;
                }

                Action action = () =>
                {
                    lock (PhoneBookContactsLock)
                    {
                        using (new PhoneContactsUpdatingDisposable())
                        {
                            if (_phoneBookContacts == null)
                            {
                                Utils.DebugPrint("Phone contacts have not yet been used by the framework/services. Therefore, and update doesn't make sense. Skipping.");
                                return;
                            }
                                
                            Utils.DebugPrint("Phone contacts have been updated! Querying phone contacts...");

                            var updatedPhoneContacts = Platform.GetPhoneBookContacts();

                            Utils.DebugPrint("Checking if any numbers/names have been updated...");

                            var updatedPhoneContactsNumbers =
                                (from phoneBookRelation in updatedPhoneContacts
                                    from phoneNumber in phoneBookRelation.PhoneNumbers
                                    select phoneNumber.Number).ToList();

                            var phoneBookContactsNumbers =
                                (from phoneBookRelation in _phoneBookContacts
                                    from phoneNumber in phoneBookRelation.PhoneNumbers
                                    select phoneNumber.Number).ToList();

                            var updatedPhoneContactsFullName =
                                (from phoneBookRelation in updatedPhoneContacts
                                    select phoneBookRelation.FullName).ToList();

                            var phoneContactsFullName =
                                (from phoneBookRelation in _phoneBookContacts
                                    select phoneBookRelation.FullName).ToList();

                            var phoneBooksEqual =
                                updatedPhoneContactsNumbers.Intersect(phoneBookContactsNumbers).Count() ==
                                updatedPhoneContactsNumbers.Union(phoneBookContactsNumbers).Count() &&
                                updatedPhoneContactsFullName.Intersect(phoneContactsFullName).Count() ==
                                updatedPhoneContactsFullName.Union(phoneContactsFullName).Count();

                            if (phoneBooksEqual)
                            {
                                Utils.DebugPrint(
                                    "Phone books are equal. No need to update contacts!");
                                return;
                            }
                                
                            _phoneBookContacts = updatedPhoneContacts;

                            Utils.DebugPrint("Got phone contacts... updating running services...");

                            var notRunningServices = ServiceManager.AllNoUnified.ToList();

                            foreach (
                                var service in
                                ServiceManager.RunningNoUnified)
                            {
                                try
                                {
                                    service.RefreshPhoneBookContacts();
                                    BubbleGroupUpdater.Update(service);
                                    ServiceEvents.RaiseContactsUpdated(service);
                                    notRunningServices.Remove(service);
                                }
                                catch (Exception ex)
                                {
                                    Utils.DebugPrint("Service " + service.Information.ServiceName +
                                        " failed to refresh it contacts. " + ex.Message);
                                }
                            }

                            if (notRunningServices.Any())
                            {
                                SettingsChangedManager.SetNeedsContactSync(notRunningServices.ToArray(), true);
                            }
                            SaveCachedPhoneBookContacts(updatedPhoneContacts);
                        }
                    }
                };

                using (Platform.AquireWakeLock("DisaContactsUpdate"))
                {
                    action();
                }
            });
        }

        public static bool PhoneNumberComparer(string left, string right)
        {
            // do an ordinal string comparison if any of the numbers are spoofed (i.e. have any letters)
            if (IsSpoofedPhoneNumber(left, right))
            {
                return left == right;
            }

            return AndroidPhoneNumberComparer.CompareLoosely(left, right);
        }

        private static bool IsSpoofedPhoneNumber(params string[] phoneNumbers)
        {
            foreach (var phoneNumber in phoneNumbers)
            {
                if (phoneNumber == null)
                    continue;
                if (phoneNumber.Any(x => char.IsLetter(x)))
                {
                    return true;
                }
            }
            return false;
        }

        /*
         * Copyright (C) 2006 The Android Open Source Project
         *
         * Licensed under the Apache License, Version 2.0 (the "License");
         * you may not use this file except in compliance with the License.
         * You may obtain a copy of the License at
         *
         *      http://www.apache.org/licenses/LICENSE-2.0
         *
         * Unless required by applicable law or agreed to in writing, software
         * distributed under the License is distributed on an "AS IS" BASIS,
         * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
         * See the License for the specific language governing permissions and
         * limitations under the License.
         */
        private static class AndroidPhoneNumberComparer
        {
            private const char PAUSE = ',';
            private const char WAIT = ';';
            private const char WILD = 'N';
            private const int MIN_MATCH = 7;

            private static int indexOfLastNetworkChar(string a)
            {
                int pIndex, wIndex;
                int origLength;
                int trimIndex;

                origLength = a.Length;

                pIndex = a.IndexOf(PAUSE);
                wIndex = a.IndexOf(WAIT);

                trimIndex = minPositive(pIndex, wIndex);

                if (trimIndex < 0)
                {
                    return origLength - 1;
                }
                else
                {
                    return trimIndex - 1;
                }
            }

            private static bool isDialable(char c)
            {
                return (c >= '0' && c <= '9') || c == '*' || c == '#' || c == '+' || c == WILD;
            }

            private static bool isNonSeparator(char c)
            {
                return (c >= '0' && c <= '9') || c == '*' || c == '#' || c == '+'
                       || c == WILD || c == WAIT || c == PAUSE;
            }

            private static bool isISODigit(char c)
            {
                return c >= '0' && c <= '9';
            }


            private static int minPositive(int a, int b)
            {
                if (a >= 0 && b >= 0)
                {
                    return (a < b) ? a : b;
                }
                else if (a >= 0)
                { /* && b < 0 */
                    return a;
                }
                else if (b >= 0)
                { /* && a < 0 */
                    return b;
                }
                else
                { /* a < 0 && b < 0 */
                    return -1;
                }
            }

            private static bool matchIntlPrefix(string a, int len)
            {
                /* '([^0-9*#+pwn]\+[^0-9*#+pwn] | [^0-9*#+pwn]0(0|11)[^0-9*#+pwn] )$' */
                /*        0       1                           2 3 45               */

                int state = 0;
                for (int i = 0; i < len; i++)
                {
                    char c = a[i];

                    switch (state)
                    {
                        case 0:
                            if (c == '+')
                                state = 1;
                            else if (c == '0')
                                state = 2;
                            else if (isNonSeparator(c))
                                return false;
                            break;

                        case 2:
                            if (c == '0')
                                state = 3;
                            else if (c == '1')
                                state = 4;
                            else if (isNonSeparator(c))
                                return false;
                            break;

                        case 4:
                            if (c == '1')
                                state = 5;
                            else if (isNonSeparator(c))
                                return false;
                            break;

                        default:
                            if (isNonSeparator(c))
                                return false;
                            break;

                    }
                }

                return state == 1 || state == 3 || state == 5;
            }

            private static bool matchTrunkPrefix(string a, int len)
            {
                bool found;

                found = false;

                for (int i = 0; i < len; i++)
                {
                    char c = a[i];

                    if (c == '0' && !found)
                    {
                        found = true;
                    }
                    else if (isNonSeparator(c))
                    {
                        return false;
                    }
                }

                return found;
            }

            private static bool matchIntlPrefixAndCC(string a, int len)
            {
                /*  [^0-9*#+pwn]*(\+|0(0|11)\d\d?\d? [^0-9*#+pwn] $ */
                /*      0          1 2 3 45  6 7  8                 */

                int state = 0;
                for (int i = 0; i < len; i++)
                {
                    char c = a[i];

                    switch (state)
                    {
                        case 0:
                            if (c == '+')
                                state = 1;
                            else if (c == '0')
                                state = 2;
                            else if (isNonSeparator(c))
                                return false;
                            break;

                        case 2:
                            if (c == '0')
                                state = 3;
                            else if (c == '1')
                                state = 4;
                            else if (isNonSeparator(c))
                                return false;
                            break;

                        case 4:
                            if (c == '1')
                                state = 5;
                            else if (isNonSeparator(c))
                                return false;
                            break;

                        case 1:
                        case 3:
                        case 5:
                            if (isISODigit(c))
                                state = 6;
                            else if (isNonSeparator(c))
                                return false;
                            break;

                        case 6:
                        case 7:
                            if (isISODigit(c))
                                state++;
                            else if (isNonSeparator(c))
                                return false;
                            break;

                        default:
                            if (isNonSeparator(c))
                                return false;
                            break;
                    }
                }

                return state == 6 || state == 7 || state == 8;
            }

            public static bool CompareLoosely(string a, string b)
            {
                int ia, ib;
                int matched;
                int numNonDialableCharsInA = 0;
                int numNonDialableCharsInB = 0;

                if (a == null || b == null)
                    return a == b;

                if (a.Length == 0 || b.Length == 0)
                {
                    return false;
                }

                ia = indexOfLastNetworkChar(a);
                ib = indexOfLastNetworkChar(b);
                matched = 0;

                while (ia >= 0 && ib >= 0)
                {
                    char ca, cb;
                    bool skipCmp = false;

                    ca = a[ia];

                    if (!isDialable(ca))
                    {
                        ia--;
                        skipCmp = true;
                        numNonDialableCharsInA++;
                    }

                    cb = b[ib];

                    if (!isDialable(cb))
                    {
                        ib--;
                        skipCmp = true;
                        numNonDialableCharsInB++;
                    }

                    if (!skipCmp)
                    {
                        if (cb != ca && ca != WILD && cb != WILD)
                        {
                            break;
                        }
                        ia--;
                        ib--;
                        matched++;
                    }
                }

                if (matched < MIN_MATCH)
                {
                    int effectiveALen = a.Length - numNonDialableCharsInA;
                    int effectiveBLen = b.Length - numNonDialableCharsInB;


                    // if the number of dialable chars in a and b match, but the matched chars < MIN_MATCH,
                    // treat them as equal (i.e. 404-04 and 40404)
                    if (effectiveALen == effectiveBLen && effectiveALen == matched)
                    {
                        return true;
                    }

                    return false;
                }

                // At least one string has matched completely;
                if (matched >= MIN_MATCH && (ia < 0 || ib < 0))
                {
                    return true;
                }

                /*
                 * Now, what remains must be one of the following for a
                 * match:
                 *
                 *  - a '+' on one and a '00' or a '011' on the other
                 *  - a '0' on one and a (+,00)<country code> on the other
                 *     (for this, a '0' and a '00' prefix would have succeeded above)
                 */

                if (matchIntlPrefix(a, ia + 1)
                    && matchIntlPrefix(b, ib + 1))
                {
                    return true;
                }

                if (matchTrunkPrefix(a, ia + 1)
                    && matchIntlPrefixAndCC(b, ib + 1))
                {
                    return true;
                }

                if (matchTrunkPrefix(b, ib + 1)
                    && matchIntlPrefixAndCC(a, ia + 1))
                {
                    return true;
                }

                /*
                 * Last resort: if the number of unmatched characters on both sides is less than or equal
                 * to the length of the longest country code and only one number starts with a + accept
                 * the match. This is because some countries like France and Russia have an extra prefix
                 * digit that is used when dialing locally in country that does not show up when you dial
                 * the number using the country code. In France this prefix digit is used to determine
                 * which land line carrier to route the call over.
                 */
                bool aPlusFirst = (a[0] == '+');
                bool bPlusFirst = (b[0] == '+');
                if (ia < 4 && ib < 4 && (aPlusFirst || bPlusFirst) && !(aPlusFirst && bPlusFirst)) 
                {
                    return true;
                }

                return false;
            }
        }
    }
}