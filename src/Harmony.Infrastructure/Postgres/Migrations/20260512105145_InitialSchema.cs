using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Harmony.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    Name = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: true
                    ),
                    NormalizedName = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: true
                    ),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    id = table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    Discriminator = table.Column<string>(
                        type: "character varying(4)",
                        maxLength: 4,
                        nullable: true
                    ),
                    avatar_key = table.Column<string>(type: "text", nullable: true),
                    banner_key = table.Column<string>(type: "text", nullable: true),
                    Bio = table.Column<string>(type: "text", nullable: true),
                    StatusMessage = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    AccountStatus = table.Column<string>(
                        type: "character varying(16)",
                        maxLength: 16,
                        nullable: false,
                        defaultValue: "active"
                    ),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    username = table.Column<string>(
                        type: "character varying(32)",
                        maxLength: 32,
                        nullable: true
                    ),
                    email = table.Column<string>(
                        type: "character varying(255)",
                        maxLength: 255,
                        nullable: true
                    ),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    RoleId = table.Column<long>(type: "bigint", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "PK_AspNetUserLogins",
                        x => new { x.LoginProvider, x.ProviderKey }
                    );
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    RoleId = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "PK_AspNetUserTokens",
                        x => new
                        {
                            x.UserId,
                            x.LoginProvider,
                            x.Name,
                        }
                    );
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "Friends",
                columns: table => new
                {
                    requester_id = table.Column<long>(type: "bigint", nullable: false),
                    addressee_id = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(
                        type: "character varying(16)",
                        maxLength: 16,
                        nullable: false
                    ),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Friends", x => new { x.requester_id, x.addressee_id });
                    table.ForeignKey(
                        name: "FK_Friends_Users_addressee_id",
                        column: x => x.addressee_id,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_Friends_Users_requester_id",
                        column: x => x.requester_id,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "Guilds",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(
                        type: "character varying(100)",
                        maxLength: 100,
                        nullable: false
                    ),
                    description = table.Column<string>(type: "text", nullable: true),
                    owner_id = table.Column<long>(type: "bigint", nullable: false),
                    icon_key = table.Column<string>(type: "text", nullable: true),
                    banner_key = table.Column<string>(type: "text", nullable: true),
                    is_public = table.Column<bool>(type: "boolean", nullable: false),
                    invite_code = table.Column<string>(
                        type: "character varying(16)",
                        maxLength: 16,
                        nullable: true
                    ),
                    member_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Guilds", x => x.id);
                    table.ForeignKey(
                        name: "FK_Guilds_Users_owner_id",
                        column: x => x.owner_id,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "NotificationPreferences",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    mentions_enabled = table.Column<bool>(
                        type: "boolean",
                        nullable: false,
                        defaultValue: true
                    ),
                    replies_enabled = table.Column<bool>(
                        type: "boolean",
                        nullable: false,
                        defaultValue: true
                    ),
                    friend_requests = table.Column<bool>(
                        type: "boolean",
                        nullable: false,
                        defaultValue: true
                    ),
                    guild_invites = table.Column<bool>(
                        type: "boolean",
                        nullable: false,
                        defaultValue: true
                    ),
                    push_enabled = table.Column<bool>(
                        type: "boolean",
                        nullable: false,
                        defaultValue: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationPreferences", x => x.user_id);
                    table.ForeignKey(
                        name: "FK_NotificationPreferences_Users_user_id",
                        column: x => x.user_id,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    type = table.Column<string>(
                        type: "character varying(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    actor_id = table.Column<long>(type: "bigint", nullable: false),
                    guild_id = table.Column<long>(type: "bigint", nullable: true),
                    channel_id = table.Column<long>(type: "bigint", nullable: true),
                    message_id = table.Column<long>(type: "bigint", nullable: true),
                    is_read = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.id);
                    table.ForeignKey(
                        name: "FK_Notifications_Users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_Notifications_Users_user_id",
                        column: x => x.user_id,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    revoked_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_RefreshTokens_Users_user_id",
                        column: x => x.user_id,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "UserBlocks",
                columns: table => new
                {
                    blocker_id = table.Column<long>(type: "bigint", nullable: false),
                    blocked_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserBlocks", x => new { x.blocker_id, x.blocked_id });
                    table.ForeignKey(
                        name: "FK_UserBlocks_Users_blocked_id",
                        column: x => x.blocked_id,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_UserBlocks_Users_blocker_id",
                        column: x => x.blocker_id,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "UserMutes",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    target_id = table.Column<long>(type: "bigint", nullable: false),
                    target_type = table.Column<string>(
                        type: "character varying(16)",
                        maxLength: 16,
                        nullable: false
                    ),
                    muted_until = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "PK_UserMutes",
                        x => new
                        {
                            x.user_id,
                            x.target_id,
                            x.target_type,
                        }
                    );
                    table.ForeignKey(
                        name: "FK_UserMutes_Users_user_id",
                        column: x => x.user_id,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "UserPushSubscriptions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    endpoint = table.Column<string>(type: "text", nullable: false),
                    p256dh = table.Column<string>(type: "text", nullable: false),
                    auth_key = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPushSubscriptions", x => x.id);
                    table.ForeignKey(
                        name: "FK_UserPushSubscriptions_Users_user_id",
                        column: x => x.user_id,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    actor_id = table.Column<long>(type: "bigint", nullable: false),
                    action_type = table.Column<string>(
                        type: "character varying(64)",
                        maxLength: 64,
                        nullable: false
                    ),
                    target_id = table.Column<long>(type: "bigint", nullable: true),
                    changes = table.Column<string>(type: "jsonb", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.id);
                    table.ForeignKey(
                        name: "FK_AuditLogs_Guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "Guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_AuditLogs_Users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    guild_id = table.Column<long>(type: "bigint", nullable: true),
                    name = table.Column<string>(
                        type: "character varying(100)",
                        maxLength: 100,
                        nullable: false
                    ),
                    topic = table.Column<string>(
                        type: "character varying(1024)",
                        maxLength: 1024,
                        nullable: true
                    ),
                    type = table.Column<string>(
                        type: "character varying(16)",
                        maxLength: 16,
                        nullable: false
                    ),
                    position = table.Column<int>(type: "integer", nullable: false),
                    category_id = table.Column<long>(type: "bigint", nullable: true),
                    is_nsfw = table.Column<bool>(type: "boolean", nullable: false),
                    slowmode_seconds = table.Column<int>(type: "integer", nullable: false),
                    bitrate = table.Column<int>(type: "integer", nullable: true),
                    user_limit = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.id);
                    table.ForeignKey(
                        name: "FK_Channels_Channels_category_id",
                        column: x => x.category_id,
                        principalTable: "Channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull
                    );
                    table.ForeignKey(
                        name: "FK_Channels_Guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "Guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "GuildMembers",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    nickname = table.Column<string>(
                        type: "character varying(32)",
                        maxLength: 32,
                        nullable: true
                    ),
                    joined_at = table.Column<long>(type: "bigint", nullable: false),
                    is_owner = table.Column<bool>(type: "boolean", nullable: false),
                    communication_disabled_until = table.Column<long>(
                        type: "bigint",
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildMembers", x => new { x.user_id, x.guild_id });
                    table.ForeignKey(
                        name: "FK_GuildMembers_Guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "Guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_GuildMembers_Users_user_id",
                        column: x => x.user_id,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(
                        type: "character varying(100)",
                        maxLength: 100,
                        nullable: false
                    ),
                    color = table.Column<int>(type: "integer", nullable: false),
                    permission_bits = table.Column<long>(type: "bigint", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    is_hoisted = table.Column<bool>(type: "boolean", nullable: false),
                    is_mentionable = table.Column<bool>(type: "boolean", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.id);
                    table.ForeignKey(
                        name: "FK_Roles_Guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "Guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "DirectMessageChannels",
                columns: table => new
                {
                    channel_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    is_hidden = table.Column<bool>(type: "boolean", nullable: false),
                    last_read_id = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "PK_DirectMessageChannels",
                        x => new { x.channel_id, x.user_id }
                    );
                    table.ForeignKey(
                        name: "FK_DirectMessageChannels_Channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "Channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_DirectMessageChannels_Users_user_id",
                        column: x => x.user_id,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "FileAttachments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    uploader_id = table.Column<long>(type: "bigint", nullable: false),
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    channel_id = table.Column<long>(type: "bigint", nullable: false),
                    minio_key = table.Column<string>(type: "text", nullable: false),
                    filename = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    is_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileAttachments", x => x.id);
                    table.ForeignKey(
                        name: "FK_FileAttachments_Channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "Channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_FileAttachments_Users_uploader_id",
                        column: x => x.uploader_id,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "GuildInvites",
                columns: table => new
                {
                    code = table.Column<string>(
                        type: "character varying(16)",
                        maxLength: 16,
                        nullable: false
                    ),
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    channel_id = table.Column<long>(type: "bigint", nullable: false),
                    creator_id = table.Column<long>(type: "bigint", nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: true),
                    use_count = table.Column<int>(type: "integer", nullable: false),
                    expires_at = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildInvites", x => x.code);
                    table.ForeignKey(
                        name: "FK_GuildInvites_Channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "Channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_GuildInvites_Guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "Guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_GuildInvites_Users_creator_id",
                        column: x => x.creator_id,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "MessagesSearch",
                columns: table => new
                {
                    message_id = table.Column<long>(type: "bigint", nullable: false),
                    channel_id = table.Column<long>(type: "bigint", nullable: false),
                    guild_id = table.Column<long>(type: "bigint", nullable: true),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessagesSearch", x => x.message_id);
                    table.ForeignKey(
                        name: "FK_MessagesSearch_Channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "Channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );
            // After the MessagesSearch table creation:
            migrationBuilder.Sql(
                @"
                    ALTER TABLE ""MessagesSearch""
                    ADD COLUMN content_search tsvector
                    GENERATED ALWAYS AS (to_tsvector('english', content)) STORED;
                "
            );

            migrationBuilder.Sql(
                @"
                    CREATE INDEX ix_messages_search_content_search
                    ON ""MessagesSearch"" USING GIN (content_search);
                "
            );

            migrationBuilder.CreateTable(
                name: "VoiceStates",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    channel_id = table.Column<long>(type: "bigint", nullable: false),
                    is_muted = table.Column<bool>(type: "boolean", nullable: false),
                    is_deafened = table.Column<bool>(type: "boolean", nullable: false),
                    is_server_muted = table.Column<bool>(type: "boolean", nullable: false),
                    is_server_deafened = table.Column<bool>(type: "boolean", nullable: false),
                    is_streaming = table.Column<bool>(type: "boolean", nullable: false),
                    is_video_on = table.Column<bool>(type: "boolean", nullable: false),
                    joined_at = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoiceStates", x => x.user_id);
                    table.ForeignKey(
                        name: "FK_VoiceStates_Channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "Channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_VoiceStates_Guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "Guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_VoiceStates_Users_user_id",
                        column: x => x.user_id,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "ChannelPermissionOverrides",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    channel_id = table.Column<long>(type: "bigint", nullable: false),
                    target_id = table.Column<long>(type: "bigint", nullable: false),
                    target_type = table.Column<string>(
                        type: "character varying(8)",
                        maxLength: 8,
                        nullable: false
                    ),
                    allow_bits = table.Column<long>(type: "bigint", nullable: false),
                    deny_bits = table.Column<long>(type: "bigint", nullable: false),
                    RoleId = table.Column<long>(type: "bigint", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelPermissionOverrides", x => x.id);
                    table.ForeignKey(
                        name: "FK_ChannelPermissionOverrides_Channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "Channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_ChannelPermissionOverrides_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "id"
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "RoleAssignments",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    role_id = table.Column<long>(type: "bigint", nullable: false),
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    assigned_at = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleAssignments", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "FK_RoleAssignments_Roles_role_id",
                        column: x => x.role_id,
                        principalTable: "Roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_RoleAssignments_Users_user_id",
                        column: x => x.user_id,
                        principalTable: "Users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId"
            );

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_actor_id",
                table: "AuditLogs",
                column: "actor_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_guild_id_created_at",
                table: "AuditLogs",
                columns: new[] { "guild_id", "created_at" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_ChannelPermissionOverrides_channel_id_target_id_target_type",
                table: "ChannelPermissionOverrides",
                columns: new[] { "channel_id", "target_id", "target_type" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_ChannelPermissionOverrides_RoleId",
                table: "ChannelPermissionOverrides",
                column: "RoleId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Channels_category_id",
                table: "Channels",
                column: "category_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Channels_guild_id",
                table: "Channels",
                column: "guild_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_DirectMessageChannels_user_id",
                table: "DirectMessageChannels",
                column: "user_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_FileAttachments_channel_id",
                table: "FileAttachments",
                column: "channel_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_FileAttachments_uploader_id",
                table: "FileAttachments",
                column: "uploader_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Friends_addressee_id",
                table: "Friends",
                column: "addressee_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_GuildInvites_channel_id",
                table: "GuildInvites",
                column: "channel_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_GuildInvites_creator_id",
                table: "GuildInvites",
                column: "creator_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_GuildInvites_guild_id",
                table: "GuildInvites",
                column: "guild_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_GuildMembers_guild_id",
                table: "GuildMembers",
                column: "guild_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Guilds_invite_code",
                table: "Guilds",
                column: "invite_code",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_Guilds_owner_id",
                table: "Guilds",
                column: "owner_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_MessagesSearch_channel_id_created_at",
                table: "MessagesSearch",
                columns: new[] { "channel_id", "created_at" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_actor_id",
                table: "Notifications",
                column: "actor_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_created_at",
                table: "Notifications",
                column: "created_at"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_user_id_is_read",
                table: "Notifications",
                columns: new[] { "user_id", "is_read" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_family_id",
                table: "RefreshTokens",
                column: "family_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_token_hash",
                table: "RefreshTokens",
                column: "token_hash",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_user_id",
                table: "RefreshTokens",
                column: "user_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_guild_id",
                table: "RoleAssignments",
                column: "guild_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_role_id",
                table: "RoleAssignments",
                column: "role_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Roles_guild_id",
                table: "Roles",
                column: "guild_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_UserBlocks_blocked_id",
                table: "UserBlocks",
                column: "blocked_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_UserPushSubscriptions_user_id",
                table: "UserPushSubscriptions",
                column: "user_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_VoiceStates_channel_id",
                table: "VoiceStates",
                column: "channel_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_VoiceStates_guild_id",
                table: "VoiceStates",
                column: "guild_id"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AspNetRoleClaims");

            migrationBuilder.DropTable(name: "AspNetUserClaims");

            migrationBuilder.DropTable(name: "AspNetUserLogins");

            migrationBuilder.DropTable(name: "AspNetUserRoles");

            migrationBuilder.DropTable(name: "AspNetUserTokens");

            migrationBuilder.DropTable(name: "AuditLogs");

            migrationBuilder.DropTable(name: "ChannelPermissionOverrides");

            migrationBuilder.DropTable(name: "DirectMessageChannels");

            migrationBuilder.DropTable(name: "FileAttachments");

            migrationBuilder.DropTable(name: "Friends");

            migrationBuilder.DropTable(name: "GuildInvites");

            migrationBuilder.DropTable(name: "GuildMembers");

            migrationBuilder.DropTable(name: "MessagesSearch");

            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_messages_search_content_search;");
            migrationBuilder.Sql(
                @"ALTER TABLE ""MessagesSearch"" DROP COLUMN IF EXISTS content_search;"
            );

            migrationBuilder.DropTable(name: "NotificationPreferences");

            migrationBuilder.DropTable(name: "Notifications");

            migrationBuilder.DropTable(name: "RefreshTokens");

            migrationBuilder.DropTable(name: "RoleAssignments");

            migrationBuilder.DropTable(name: "UserBlocks");

            migrationBuilder.DropTable(name: "UserMutes");

            migrationBuilder.DropTable(name: "UserPushSubscriptions");

            migrationBuilder.DropTable(name: "VoiceStates");

            migrationBuilder.DropTable(name: "AspNetRoles");

            migrationBuilder.DropTable(name: "Roles");

            migrationBuilder.DropTable(name: "Channels");

            migrationBuilder.DropTable(name: "Guilds");

            migrationBuilder.DropTable(name: "Users");
        }
    }
}
