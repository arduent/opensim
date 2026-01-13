/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Text;
using OpenMetaverse;


namespace OpenSim.MoneyDataWrapper
{
    public class TransactionData
    {
        UUID m_uuid;
        string m_sender       = string.Empty;
        string m_receiver     = string.Empty;
        int m_amount;
        int m_senderBalance;
        int m_receiverBalance;
        int m_type;
        int m_time;
        int m_status;
        string m_objectID     = UUID.Zero.ToString();
//      string m_objectID     = "00000000-0000-0000-0000-000000000000";
        string m_objectName   = string.Empty;
        string m_regionHandle = string.Empty;
        string m_regionUUID   = string.Empty;
        string m_secureCode   = string.Empty;
        string m_commonName   = string.Empty;
        string m_description  = string.Empty;

/*
        public TransactionData(string uuid, string sender, string receiver,
            int amount, int time, int status, string description)
        {
            this.m_uuid = uuid;
            this.m_sender = sender;
            this.m_receiver = receiver;
            this.m_amount = amount;
        }
*/

        public UUID TransUUID
        {
            get { return m_uuid; }
            set { m_uuid = value; }
        }

        public string Sender
        {
            get { return m_sender; }
            set { m_sender = value; }
        }

        public string Receiver
        {
            get { return m_receiver; }
            set { m_receiver = value; }
        }

        public int Amount
        {
            get { return m_amount; }
            set { m_amount = value; }
        }

        public int SenderBalance
        {
            get { return m_senderBalance; }
            set { m_senderBalance = value; }
        }

        public int ReceiverBalance
        {
            get { return m_receiverBalance; }
            set { m_receiverBalance = value; }
        }

        public int Type
        {
            get { return m_type; }
            set { m_type = value; }
        }

        public int Time
        {
            get { return m_time; }
            set { m_time = value; }
        }

        public int Status
        {
            get { return m_status; }
            set { m_status = value; }
        }

        public string Description
        {
            get { return m_description; }
            set { m_description = value; }
        }

        public string ObjectUUID
        {
            get { return m_objectID; }
            set { m_objectID = value; }
        }

        public string ObjectName
        {
            get { return m_objectName; }
            set { m_objectName = value; }
        }

        public string RegionHandle
        {
            get { return m_regionHandle; }
            set { m_regionHandle = value; }
        }

        public string RegionUUID
        {
            get { return m_regionUUID; }
            set { m_regionUUID = value; }
        }

        public string SecureCode
        {
            get { return m_secureCode; }
            set { m_secureCode = value; }
        }

        public string CommonName
        {
            get { return m_commonName; }
            set { m_commonName = value; }
        }
    }

        /// <summary>
    /// Transaction Type
    /// </summary>
    public enum TransactionType : int
    {
        None                = 0,
        // Extend
        BirthGift           = 900,
        AwardPoints         = 901,
        // One-Time Charges
        ObjectClaim         = 1000,
        LandClaim           = 1001,
        GroupCreate         = 1002,
        GroupJoin           = 1004,
        TeleportCharge      = 1100,
        UploadCharge        = 1101,
        LandAuction         = 1102,
        ClassifiedCharge    = 1103,
        // Recurrent Charges
        ObjectTax           = 2000,
        LandTax             = 2001,
        LightTax            = 2002,
        ParcelDirFee        = 2003,
        GroupTax            = 2004,
        ClassifiedRenew     = 2005,
        ScheduledFee        = 2900,
        // Inventory Transactions
        GiveInventory       = 3000,
        // Transfers Between Users
        ObjectSale          = 5000,
        Gift                = 5001,
        LandSale            = 5002,
        ReferBonus          = 5003,
        InvntorySale        = 5004,
        RefundPurchase      = 5005,
        LandPassSale        = 5006,
        DwellBonus          = 5007,
        PayObject           = 5008,
        ObjectPays          = 5009,
        BuyMoney            = 5010,
        MoveMoney           = 5011,
        SendMoney           = 5012,
        // Group Transactions
        GroupLandDeed       = 6001,
        GroupObjectDeed     = 6002,
        GroupLiability      = 6003,
        GroupDividend       = 6004,
        GroupMembershipDues = 6005,
        // Stipend Credits
        StipendBasic        = 10000
    }

    public enum Status
    { 
        SUCCESS_STATUS = 0, 
        PENDING_STATUS = 1, 
        FAILED_STATUS  = 2,
        ERROR_STATUS   = 9
    }


    public enum AvatarType
    { 
        LOCAL_AVATAR   = 0, 
        HG_AVATAR      = 1, 
        NPC_AVATAR     = 2, 
        GUEST_AVATAR   = 3,
        FOREIGN_AVATAR = 8,
        UNKNOWN_AVATAR = 9
    }


    public class UserInfo
    {
        string m_userID = string.Empty;
        string m_simIP = string.Empty;
        string m_avatarName = string.Empty;
        string m_passwordHash = string.Empty;
        int    m_avatarType  = (int)AvatarType.LOCAL_AVATAR;
        int    m_avatarClass = (int)AvatarType.LOCAL_AVATAR;
        string m_serverURL = string.Empty;

        public string UserID
        {
            get { return m_userID; }
            set { m_userID = value; }
        }

        public string SimIP
        {
            get { return m_simIP; }
            set { m_simIP = value; }
        }

        public string Avatar
        {
            get { return m_avatarName; }
            set { m_avatarName = value; }
        }

        public string PswHash
        {
            get { return m_passwordHash; }
            set { m_passwordHash = value; }
        }

        public int Type
        {
            get { return m_avatarType; }
            set { m_avatarType = value; }
        }

        public int Class
        {
            get { return m_avatarClass; }
            set { m_avatarClass = value; }
        }

        public string ServerURL
        {
            get { return m_serverURL; }
            set { m_serverURL = value; }
        }
    }
}
