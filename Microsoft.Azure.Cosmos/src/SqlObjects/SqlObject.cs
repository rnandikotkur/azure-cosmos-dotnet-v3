﻿//-----------------------------------------------------------------------------------------------------------------------------------------
// <copyright file="SqlObject.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------------------------------------------------------------------------
namespace Microsoft.Azure.Cosmos.Sql
{
    using System.Text;

    internal abstract class SqlObject
    {
        protected SqlObject(SqlObjectKind kind)
        {
            this.Kind = kind;
        }

        public SqlObjectKind Kind
        {
            get;
        }

        public abstract void Accept(SqlObjectVisitor visitor);

        public abstract TResult Accept<TResult>(SqlObjectVisitor<TResult> visitor);

        public abstract TResult Accept<T, TResult>(SqlObjectVisitor<T, TResult> visitor, T input);

        public override string ToString()
        {
            SqlObjectTextSerializer sqlObjectTextSerializer = new SqlObjectTextSerializer();
            this.Accept(sqlObjectTextSerializer);
            return sqlObjectTextSerializer.ToString();
        }

        public override int GetHashCode()
        {
            return this.Accept(SqlObjectHasher.Singleton);
        }

        public SqlObject GetObfuscatedObject()
        {
            SqlObjectObfuscator sqlObjectObfuscator = new SqlObjectObfuscator();
            return this.Accept(sqlObjectObfuscator);
        }
    }
}
