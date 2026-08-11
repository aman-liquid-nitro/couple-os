# Couple OS — Product & Technical Specification

## 1. Overview

**Couple OS** is a private, AI-powered shared operating system for a couple.

The goal is not to build another chatbot, task manager, expense tracker, or calendar. The goal is to create a single system that helps two people capture, remember, organize, plan, and act on the everyday details of their shared life.

The central interaction should be natural language:

> "We're almost out of detergent."

> "Let's visit my parents next month."

> "How much did we spend eating out this month?"

> "Our anniversary is coming up. Can you suggest something?"

> "What do we need to take care of this week?"

The system should understand the intent, update the appropriate structured data, preserve useful memories, and proactively surface relevant information later.

### Core principle

**Capture → Understand → Remember → Organize → Act → Learn**

---

# 2. Product Vision

Couple OS should eventually feel like a quiet, reliable digital assistant that understands the couple's shared life.

It should know about:

- People
- Family
- Important dates
- Shared goals
- Household responsibilities
- Shopping
- Expenses
- Bills
- Trips
- Plans
- Decisions
- Preferences
- Memories
- Commitments
- Future intentions

It should be able to answer:

- "What are we doing this weekend?"
- "What bills are coming up?"
- "What did we decide about buying a car?"
- "What things have we been meaning to do?"
- "What should we buy this week?"
- "Can we afford this trip?"
- "When is her sister's birthday?"
- "What did I promise to do?"
- "What are our current goals?"
- "What needs attention this week?"

It should also proactively surface useful information:

- "The electricity bill is usually due this week."
- "You haven't planned anything for your anniversary yet."
- "You mentioned visiting your parents next month."
- "You're probably going to run out of detergent soon."
- "Dining expenses are significantly higher than last month."

The system must be useful without becoming noisy or intrusive.

---

# 3. Product Philosophy

## 3.1 Not another chatbot

The LLM is an intelligence layer, not the product itself.

The actual product is:

- Structured data
- Memory
- Context
- Tools
- Agents
- Notifications
- Privacy
- A simple interface

The LLM should interact with the application through controlled tools.

Bad architecture:

```text
User
  ↓
LLM
  ↓
Direct database manipulation
```

Preferred architecture:

```text
User
  ↓
LLM
  ↓
Tool/API layer
  ↓
Validation
  ↓
Application services
  ↓
Database
```

---

## 3.2 Natural language first

Users should not need to understand the application's internal data model.

For example:

> "We're almost out of shampoo."

The system should infer:

```text
Type: Shopping Item
Item: Shampoo
Status: Needed
Scope: Shared
```

Another example:

> "Remind me to book my dentist appointment."

Should become:

```text
Type: Reminder
Task: Book dentist appointment
Owner: Current user
Due date: Unknown
Needs clarification: Yes
```

---

## 3.3 Shared and private data

The system must distinguish between:

### Shared data

Visible to both partners.

Examples:

- Shared expenses
- Household tasks
- Trips
- Shared goals
- Shopping list
- Shared events
- Shared memories
- Household bills

### Private data

Visible only to the owner.

Examples:

- Surprise gifts
- Private notes
- Personal goals
- Private reflections
- Private conversations
- Personal reminders

The LLM must respect visibility boundaries.

A private surprise must never leak through shared context.

---

# 4. Initial Users

The initial target user is a couple living together or managing a shared life.

Typical use cases:

- Married couples
- Long-term partners
- Couples managing a household
- Couples planning finances
- Couples planning trips
- Couples sharing responsibilities

The first version should optimize for **two-person households**.

Do not build family/group support in V1 unless the architecture makes it trivial.

---

# 5. Core Product Modules

The initial architecture should support these modules.

```text
Couple OS
│
├── Identity & Couple
├── Memory
├── Life Inbox
├── Tasks & Commitments
├── Shopping
├── Household
├── Expenses & Finance
├── Calendar & Events
├── Goals
├── Planning
├── Notifications
├── AI / Agent Layer
└── Privacy & Permissions
```

---

# 6. Module: Identity & Couple

## Requirements

A couple consists of exactly two users in V1.

Each user has:

```text
User
- id
- name
- email/username
- timezone
- locale
- preferences
- createdAt
```

A couple has:

```text
Couple
- id
- partnerAId
- partnerBId
- createdAt
- relationshipStartDate
- anniversaryDate
- settings
```

## Future support

Potential future models:

- Family
- Children
- Parents
- Friends
- Shared households
- Multiple households

Do not implement these initially unless needed.

---

# 7. Module: Life Inbox

This should be the primary interface for V1.

The user sees something like:

```text
What's on your mind?
```

They can enter arbitrary natural language.

Examples:

> "Buy detergent."

> "We should go to Goa in December."

> "I spent ₹2400 on dinner."

> "Remind me to call Dad tomorrow."

> "Her birthday is September 12."

> "We should start saving for a car."

The AI classifies the input and determines what action is required.

Possible intents:

```text
TASK
REMINDER
SHOPPING_ITEM
EXPENSE
EVENT
MEMORY
GOAL
PLAN
DECISION
NOTE
QUESTION
UNKNOWN
```

The AI may also generate multiple actions from one message.

Example:

> "Let's go to Goa in December. We should probably save ₹50k and book hotels in October."

Could produce:

```text
PLAN:
Goa trip

GOAL:
Trip fund = ₹50,000

REMINDER:
Review hotel bookings in October
```

---

# 8. Module: Memory

Memory is one of the most important parts of Couple OS.

Do not treat all conversations as permanent memories.

The system should distinguish between:

### Episodic memory

Something that happened.

Example:

```text
"We visited Munnar in July 2026."
```

### Semantic memory

A stable fact.

Example:

```text
"Partner prefers window seats."
```

### Preference

Example:

```text
"Partner likes Italian food."
```

### Decision

Example:

```text
"We decided not to buy a car this year."
```

### Commitment

Example:

```text
"Aman said he would book the hotel."
```

### Plan

Example:

```text
"We want to visit Goa in December."
```

### Important event

Example:

```text
"Wedding anniversary is December 14."
```

### Temporary context

Example:

```text
"We are currently comparing washing machines."
```

Temporary context should not automatically become permanent memory.

---

# 9. Memory Lifecycle

Every candidate memory should have metadata.

```text
Memory
- id
- coupleId
- ownerUserId
- type
- content
- source
- confidence
- importance
- visibility
- createdAt
- updatedAt
- expiresAt
- embedding
```

Possible visibility:

```text
PRIVATE
SHARED
```

Possible source:

```text
USER_INPUT
CHAT
IMPORTED_DOCUMENT
MANUAL
SYSTEM_INFERENCE
```

Important distinction:

**AI inference should not automatically be treated as confirmed fact.**

For example:

> "I think she prefers Italian food."

Should be stored as a low-confidence inferred preference unless confirmed.

---

# 10. Memory Retrieval

The AI should retrieve memory using a hybrid strategy:

1. Structured filtering
2. Semantic/vector search
3. Recency
4. Importance
5. User/couple scope
6. Visibility permissions

Example:

User asks:

> "Why didn't we buy the car?"

Retrieval should find:

- Relevant decision
- Date
- Financial context
- Alternative decisions
- Related goals

Not simply retrieve the nearest chat message.

---

# 11. Module: Tasks & Commitments

Tasks represent concrete actions.

```text
Task
- id
- coupleId
- ownerUserId
- title
- description
- status
- priority
- dueAt
- recurrence
- source
- createdAt
- completedAt
```

Statuses:

```text
TODO
IN_PROGRESS
DONE
CANCELLED
SNOOZED
```

Important distinction:

### Task

> "Buy detergent."

### Commitment

> "I said I would call my parents."

Commitments should be represented separately or as a task subtype because they carry social/contextual significance.

---

# 12. Module: Shopping

Shopping should support:

```text
ShoppingItem
- id
- coupleId
- name
- quantity
- category
- addedBy
- assignedTo
- status
- recurring
- estimatedConsumptionDays
- createdAt
- purchasedAt
```

Examples:

```text
Milk
Rice
Shampoo
Toothpaste
Detergent
```

The AI should eventually learn purchasing patterns.

Example:

> "You normally buy detergent every 35 days. Your last purchase was 32 days ago."

This should be an optional prediction, not an authoritative fact.

---

# 13. Module: Household

Household responsibilities may include:

- Cleaning
- Laundry
- Cooking
- Bills
- Maintenance
- Shopping
- Repairs

A chore model:

```text
Chore
- id
- coupleId
- name
- assignedTo
- recurrence
- lastCompletedAt
- nextDueAt
- status
```

Natural language should update chores.

Example:

> "I cleaned the kitchen."

The system should recognize the existing chore and mark it complete.

---

# 14. Module: Expenses & Finance

V1 should avoid direct bank integrations.

Start with:

- Manual entry
- Natural language
- CSV import
- Receipt OCR later

Expense:

```text
Expense
- id
- coupleId
- amount
- currency
- category
- description
- paidBy
- shared
- date
- merchant
- source
- createdAt
```

Example:

> "Dinner was ₹2400."

The AI should infer:

```text
amount = 2400
currency = INR
category = Dining
description = Dinner
shared = true
```

If the payer is ambiguous, ask.

---

# 15. Finance Capabilities

Eventually support questions like:

> "How much did we spend on food this month?"

> "How much did we spend eating out?"

> "Are we spending more than last month?"

> "Can we afford a ₹50k vacation?"

> "How much have we saved toward our trip?"

> "What subscriptions are we paying for?"

The AI should use deterministic calculations for numbers.

Do not ask the LLM to perform important financial arithmetic when the application can calculate it.

Preferred:

```text
LLM:
Understand question
        ↓
Finance tool
        ↓
Database query
        ↓
Deterministic calculation
        ↓
LLM:
Explain result
```

---

# 16. Module: Calendar & Events

Events:

```text
Event
- id
- coupleId
- title
- description
- startAt
- endAt
- location
- participants
- visibility
- recurrence
- source
```

Important events include:

- Birthdays
- Anniversaries
- Appointments
- Trips
- Family events
- Bill due dates
- Renewals

Future integration:

- Google Calendar
- Apple Calendar
- Outlook Calendar

Do not make external calendar integration mandatory for V1.

---

# 17. Module: Goals

Goals represent longer-term objectives.

Example:

```text
Goal
- id
- coupleId
- name
- description
- targetAmount
- currentAmount
- targetDate
- status
- owner
- createdAt
```

Examples:

```text
Emergency Fund
Trip Fund
New Laptop
Car
Home
Vacation
```

The AI should understand:

> "We saved another ₹20k for the trip."

And update the appropriate goal if the context is unambiguous.

---

# 18. Module: Plans

Plans represent multi-step objectives.

Example:

```text
Plan:
Goa Trip

Tasks:
- Decide dates
- Book transport
- Book hotel
- Plan activities
- Prepare leave request
- Create packing list
```

Plans can contain:

```text
Tasks
Events
Budget
Goals
Notes
Decisions
```

The AI should be able to generate plans from natural language.

Example:

> "Let's go to Kerala for 6 days in October."

Possible response:

```text
I created a Kerala trip plan.

Next steps:
1. Decide dates
2. Estimate budget
3. Check leave
4. Book transport
5. Book accommodation
```

---

# 19. Module: Couple Planning

Couple OS should eventually help with:

### Date nights

Consider:

- Budget
- Available time
- Preferences
- Previous activities
- Location
- Weather
- Calendar

### Trips

Consider:

- Budget
- Dates
- Leave
- Preferences
- Previous destinations

### Gifts

Track private gift ideas.

Example:

> "She mentioned she really likes that bag."

Store:

```text
Private memory:
Potential gift
Category: Bag
Mentioned: March 2026
```

This memory must not enter shared context.

---

# 20. Relationship Intelligence

This should be implemented carefully.

Couple OS is NOT a therapist.

Do not diagnose relationship problems.

Do not infer sensitive emotional states as facts.

Useful features:

- Date-night suggestions
- Shared activity suggestions
- Anniversary planning
- Gift reminders
- Shared goals
- Conversation reminders
- Unfinished plans

Avoid:

- "Your relationship is unhealthy."
- "Your partner seems angry."
- Psychological profiling
- Hidden sentiment monitoring

The system should remain supportive and transparent.

---

# 21. Notifications

Notifications should be useful and low-noise.

Types:

```text
TASK_DUE
EVENT_UPCOMING
BILL_DUE
GOAL_UPDATE
PLANNING_PROMPT
PREDICTION
IMPORTANT_MEMORY
FOLLOW_UP
```

Examples:

> "Your electricity bill is usually due this week."

> "Your anniversary is in 18 days."

> "You mentioned visiting your parents next month. Want to check weekends?"

Notifications should support:

```text
Enable
Disable
Snooze
Frequency settings
Quiet hours
```

The AI should not generate unlimited notifications.

---

# 22. AI Architecture

The AI layer should be provider-independent.

Suggested abstraction:

```csharp
public interface ILLMProvider
{
    Task<LLMResponse> CompleteAsync(
        LLMRequest request,
        CancellationToken cancellationToken);
}
```

Potential providers:

```text
OpenAI
Anthropic
Gemini
Ollama
Other OpenAI-compatible APIs
```

Model selection should be configurable.

Use cheaper/smaller models for:

- Classification
- Extraction
- Summaries
- Simple routing

Use stronger models for:

- Planning
- Complex reasoning
- Ambiguous questions
- Multi-step agent workflows

Use local models where privacy/cost makes sense.

---

# 23. Tool Architecture

The LLM should interact through explicit tools.

Examples:

```text
create_task
update_task
complete_task
create_memory
search_memory
create_expense
query_expenses
create_event
search_events
create_shopping_item
complete_shopping_item
create_goal
update_goal
create_plan
search_people
search_decisions
create_reminder
```

Each tool must have:

- Strict input schema
- Validation
- Authorization
- Logging
- Error handling
- Idempotency where appropriate

---

# 24. Example Tool Flow

User:

> "We're almost out of detergent."

System:

```text
User Input
    ↓
LLM
    ↓
Intent:
SHOPPING_ITEM
    ↓
Tool:
create_shopping_item
    ↓
Validation
    ↓
Database
    ↓
Confirmation
```

Response:

> "Added detergent to the shared shopping list."

---

# 25. Example Multi-Action Flow

User:

> "Let's go to Goa in December. We should save ₹50k and book hotels in October."

AI extracts:

```text
PLAN
Goa Trip

GOAL
Trip fund
Target: ₹50,000

TASK/REMINDER
Review hotel booking
Month: October
```

The application executes each action independently.

If one fails, the system should not silently pretend the entire operation succeeded.

---

# 26. Agent Architecture

Agents should be implemented as controlled workflows, not unrestricted autonomous loops.

Potential agents:

```text
HouseholdAgent
FinanceAgent
PlanningAgent
ShoppingAgent
EventAgent
MemoryAgent
RelationshipPlanningAgent
```

Each agent should have:

- Defined tools
- Defined permissions
- Context requirements
- Maximum execution steps
- Timeout
- Logging
- Human confirmation requirements

---

# 27. Proactive Intelligence

Eventually the system can run scheduled analysis.

Example:

```text
Every morning
    ↓
Check events
Check tasks
Check bills
Check goals
Check commitments
Check important upcoming dates
Check pending plans
    ↓
Generate candidate insights
    ↓
Rank by usefulness
    ↓
Apply notification rules
    ↓
Notify if useful
```

Do not send notifications merely because something changed.

A notification should pass a usefulness threshold.

---

# 28. Privacy Model

Privacy is a first-class architectural requirement.

## Data scopes

```text
PRIVATE_USER
SHARED_COUPLE
SYSTEM
```

Every piece of data must have a visibility scope.

Before retrieval:

```text
User
 ↓
Authorization
 ↓
Allowed data scope
 ↓
Retrieval
 ↓
LLM context
```

Never retrieve private partner data just because it is semantically relevant.

---

# 29. Encryption & Security

Recommended:

- HTTPS everywhere
- Encryption at rest
- Secure secret storage
- Hashed passwords if using password authentication
- OAuth where appropriate
- Database access control
- Row-level authorization
- Audit logging
- Secure session management
- Rate limiting
- Input validation

Sensitive fields should be considered for application-level encryption.

---

# 30. Memory Management UX

Users should be able to inspect memory.

Example:

```text
Memory

"You prefer window seats."

Source:
Conversation — March 4

Confidence:
High

Visibility:
Shared

[Edit] [Forget]
```

Users must be able to:

- Edit
- Forget
- Mark private
- Mark shared
- Correct
- Export

Important:

**The user must remain in control of their memory.**

---

# 31. Data Deletion

Implement deletion early.

Users should be able to say:

> "Forget everything about our Goa trip."

The system should identify related:

- Memories
- Tasks
- Events
- Plans
- Notes
- Embeddings

and provide a confirmation before destructive deletion.

Potential command:

```text
delete_context(
    entity_type = "trip",
    entity_id = "..."
)
```

---

# 32. Auditability

Every AI-generated mutation should be traceable.

Example:

```text
AuditEntry
- id
- userId
- action
- tool
- entity
- entityId
- input
- result
- timestamp
```

Example:

```text
2026-08-11 10:45

AI action:
create_shopping_item

Input:
"detergent"

Created:
ShoppingItem #123

Triggered by:
User message #987
```

This is essential for debugging trust issues.

---

# 33. Suggested Database Model

Start with PostgreSQL.

Potential tables:

```text
users
couples
couple_members

memories
memory_embeddings

tasks
commitments

shopping_lists
shopping_items

chores

expenses
expense_categories

events

goals
goal_transactions

plans
plan_items

notifications

conversation_sessions
conversation_messages

ai_actions
audit_logs
```

Use `pgvector` for embeddings initially.

Do not introduce a dedicated vector database unless scale actually requires it.

---

# 34. API Layer

Suggested REST API initially.

Examples:

```text
POST   /api/chat
POST   /api/tasks
GET    /api/tasks
PATCH  /api/tasks/{id}

POST   /api/memories
GET    /api/memories/search
DELETE /api/memories/{id}

POST   /api/expenses
GET    /api/expenses
GET    /api/expenses/summary

POST   /api/events
GET    /api/events

POST   /api/goals
PATCH  /api/goals/{id}

GET    /api/dashboard
GET    /api/notifications
```

The AI should use internal application services/tools rather than calling arbitrary HTTP endpoints where possible.

---

# 35. Frontend

The UI should remain simple.

Suggested main navigation:

```text
Home
Inbox
Tasks
Shopping
Money
Plans
Memories
Settings
```

Home dashboard:

```text
Good morning ❤️

Today
────────────
2 tasks
1 event
1 reminder

This week
────────────
3 upcoming events
2 pending tasks

Household
────────────
4 shopping items

Goals
────────────
Trip fund: ₹42k / ₹75k

AI insight
────────────
Your anniversary is in 18 days.
```

The chat/inbox should always be easily accessible.

---

# 36. V0 — Prototype

The first prototype should be intentionally tiny.

Implement only:

```text
Authentication
Couple creation
Simple chat/inbox
LLM integration
PostgreSQL
Tool calling

Tools:
- create_task
- create_reminder
- create_memory
- create_expense
- create_event
- create_shopping_item
- search_memory
```

No:

- Bank integration
- Calendar integration
- WhatsApp
- Mobile app
- Complex agents
- Advanced relationship features

The objective is to prove that the natural-language interaction is useful.

---

# 37. V1 — Useful Personal Product

Add:

```text
Shared/private permissions
Memory UI
Tasks
Shopping
Expenses
Events
Goals
Dashboard
Notifications
Basic proactive insights
```

Target outcome:

A couple can actually use Couple OS every day.

---

# 38. V2 — Intelligent Couple OS

Add:

```text
Planning
Trip planning
Date planning
Gift intelligence
Household predictions
Recurring expense detection
Recurring shopping prediction
Commitment tracking
Decision memory
Advanced memory retrieval
```

---

# 39. V3 — Integrations

Potential integrations:

```text
Google Calendar
Google Drive
Email
Telegram
WhatsApp
Bank/CSV imports
Receipt OCR
Cloud storage
Maps
Weather
Shopping platforms
```

Integrations should be opt-in.

---

# 40. V4 — Local AI / Privacy Mode

Support local models.

Architecture:

```text
                    Couple OS
                        │
             ┌──────────┴──────────┐
             ▼                     ▼
        Cloud AI                Local AI
             │                     │
        Strong reasoning       Private mode
```

Possible local runtime:

```text
Ollama
Other OpenAI-compatible local servers
```

Users should be able to choose which data can leave their infrastructure.

---

# 41. Recommended Tech Stack

A reasonable initial stack:

### Backend

```text
ASP.NET Core
C#
Entity Framework Core
PostgreSQL
pgvector
```

### Frontend

```text
Next.js
React
TypeScript
```

### AI

Provider abstraction supporting:

```text
OpenAI
Anthropic
Gemini
Ollama
OpenAI-compatible endpoints
```

### Background jobs

Potentially:

```text
Hangfire
Quartz.NET
or a simple hosted service initially
```

### Authentication

Potentially:

```text
OAuth
Magic links
Email/password
```

Choose the simplest secure option for V1.

### Deployment

Potential options:

```text
Docker
PostgreSQL
Cloud VPS / managed cloud
```

Do not optimize for Kubernetes or complex infrastructure initially.

---

# 42. Suggested Repository Structure

```text
couple-os/
│
├── docs/
│   ├── PRODUCT.md
│   ├── ARCHITECTURE.md
│   ├── SECURITY.md
│   ├── MEMORY.md
│   └── ADR/
│
├── src/
│   ├── CoupleOS.Api/
│   ├── CoupleOS.Application/
│   ├── CoupleOS.Domain/
│   ├── CoupleOS.Infrastructure/
│   ├── CoupleOS.AI/
│   └── CoupleOS.Workers/
│
├── web/
│   └── couple-os-web/
│
├── tests/
│   ├── CoupleOS.UnitTests/
│   ├── CoupleOS.IntegrationTests/
│   └── CoupleOS.AITests/
│
├── docker/
│
└── README.md
```

Use clean architecture only where it improves maintainability. Avoid unnecessary abstractions.

---

# 43. AI Context Pipeline

The AI request pipeline should roughly be:

```text
User Message
     ↓
Authentication
     ↓
Couple/User Context
     ↓
Intent Detection
     ↓
Relevant Memory Retrieval
     ↓
Relevant Structured Data
     ↓
Conversation Context
     ↓
LLM
     ↓
Tool Calls
     ↓
Validation
     ↓
Application Services
     ↓
Database
     ↓
Optional Memory Extraction
     ↓
Response
```

Do not send the entire database or entire conversation history to the LLM.

Use context selection.

---

# 44. Memory Extraction Pipeline

After important conversations:

```text
Conversation
    ↓
Memory candidate extraction
    ↓
Candidate classification
    ↓
Confidence scoring
    ↓
Deduplication
    ↓
Conflict detection
    ↓
Persist
```

Example:

Existing:

```text
Preference:
Likes Italian food
```

New:

> "I don't really like Italian food anymore."

Do not create a duplicate.

Update the existing preference and record the change.

---

# 45. Contradiction Handling

Memory can become stale.

The system must support:

```text
Current fact
Previous fact
Confidence
UpdatedAt
```

Example:

```text
Old:
Likes coffee

New:
Doesn't drink coffee anymore
```

The new information should supersede the old information where appropriate.

Do not silently retain contradictory facts.

---

# 46. AI Safety Rules

The AI should:

- Never fabricate memories.
- Never claim an action happened if the tool failed.
- Never expose private partner data.
- Never invent financial transactions.
- Never invent calendar events.
- Never make important destructive changes without confirmation.
- Never perform sensitive external actions without explicit authorization.
- Clearly distinguish facts from predictions.
- Clearly distinguish remembered information from inference.

For destructive actions:

```text
User:
"Delete all our memories."

AI:
"This will permanently delete 184 shared memories.
Are you sure?"
```

---

# 47. Confirmation Policy

Not every action requires confirmation.

### No confirmation

Low-risk:

```text
Add shopping item
Create normal task
Create note
Create low-risk memory
```

### Confirmation

Medium-risk:

```text
Create financial goal
Delete memory
Modify important event
Change shared preference
```

### Explicit authorization

High-risk:

```text
External purchases
Financial transactions
Sending messages
Deleting large amounts of data
External account changes
```

---

# 48. Evaluation Strategy

The system needs automated tests for AI behavior.

Create a test dataset containing natural language inputs.

Examples:

```text
"We're out of milk."
"Remind me to call Dad tomorrow."
"I spent 2400 on dinner."
"Our anniversary is December 14."
"She mentioned wanting a Kindle."
"Let's save 50000 for our Goa trip."
"I cleaned the kitchen."
"Why didn't we buy a car?"
```

For each test, verify:

```text
Intent
Entities
Tool selection
Tool parameters
Visibility
Memory behavior
Response
```

Also test adversarial cases:

```text
Private data leakage
Ambiguous names
Duplicate memories
Conflicting memories
Malformed amounts
Unknown dates
Failed tool calls
Prompt injection
Unauthorized actions
```

---

# 49. Observability

Track:

```text
LLM latency
Token usage
Model
Tool calls
Tool failures
Memory retrieval quality
Notification frequency
User corrections
AI action success rate
```

Important metrics:

### Product metrics

```text
Daily active couples
Messages per couple
Actions successfully completed
Tasks created/completed
Memory corrections
Notification dismissals
Weekly retention
```

### AI metrics

```text
Intent accuracy
Tool-call accuracy
Argument accuracy
Memory precision
Memory recall
Permission violations
Hallucination rate
```

---

# 50. Cost Management

Use model routing.

Example:

```text
Simple classification
→ Small/cheap model

Extraction
→ Small/medium model

Summarization
→ Small/medium model

Complex planning
→ Strong model

Hard reasoning
→ Strong model

Private/simple local task
→ Local model
```

Cache where appropriate.

Do not embed or reprocess unchanged content unnecessarily.

Track per-couple AI cost.

---

# 51. Potential Future Features

Do not implement these in V1, but keep architecture extensible.

### Voice

> "Hey, add detergent to the list."

### Receipt scanning

Photo → OCR → expense.

### WhatsApp integration

Messages → Couple OS.

### Calendar integration

Automatic event synchronization.

### Email understanding

Bills, reservations, confirmations.

### Smart home

Potential future household integrations.

### Family mode

Add parents/children.

### Shared documents

Insurance, rental agreements, receipts, warranties.

### Warranty tracking

```text
Washing machine
Purchased: 2026-03-12
Warranty: 2 years
```

### Subscription tracking

Detect recurring payments.

### Home maintenance

```text
AC service
Water purifier
Vehicle service
Appliance maintenance
```

---

# 52. Product Principles

Always prioritize:

1. **Useful over impressive**
2. **Trust over autonomy**
3. **Privacy over convenience**
4. **Structured data over chat history**
5. **Explicit actions over hallucinated actions**
6. **Low notification noise**
7. **User control over AI assumptions**
8. **Simple UI over feature overload**
9. **Incremental automation**
10. **Reliable memory over huge context windows**

---

# 53. First Development Milestone

The first milestone is:

> **Two people can use Couple OS for one week through natural language and actually find it useful.**

Minimum flow:

```text
User A signs up
        ↓
Creates couple
        ↓
Invites User B
        ↓
User A:
"We're almost out of detergent."
        ↓
Shopping item created

User B:
"Add toothpaste too."
        ↓
Shopping item created

User A:
"I spent ₹2400 on dinner."
        ↓
Expense created

User B:
"Remind us to book the hotel in October."
        ↓
Reminder created

User A:
"What do we need to take care of this week?"
        ↓
AI retrieves:
- Tasks
- Events
- Reminders
- Bills
- Plans
- Important dates
        ↓
Useful weekly summary
```

If this works well, build outward.

---

# 54. Development Roadmap

## Milestone 0 — Architecture

- [ ] Repository setup
- [ ] Backend project
- [ ] Frontend project
- [ ] PostgreSQL
- [ ] EF Core
- [ ] Authentication
- [ ] Basic CI
- [ ] Docker development environment
- [ ] Environment configuration
- [ ] Logging

## Milestone 1 — Couple

- [ ] User registration
- [ ] Login
- [ ] Couple creation
- [ ] Partner invitation
- [ ] Shared/private authorization
- [ ] Couple settings

## Milestone 2 — AI Inbox

- [ ] Chat UI
- [ ] LLM abstraction
- [ ] Provider configuration
- [ ] Tool calling
- [ ] Conversation storage
- [ ] Intent detection
- [ ] Error handling

## Milestone 3 — Core Tools

- [ ] Tasks
- [ ] Reminders
- [ ] Shopping
- [ ] Expenses
- [ ] Events
- [ ] Memories

## Milestone 4 — Memory

- [ ] Memory schema
- [ ] Embeddings
- [ ] pgvector
- [ ] Semantic retrieval
- [ ] Structured retrieval
- [ ] Memory extraction
- [ ] Deduplication
- [ ] Conflict handling
- [ ] Memory UI
- [ ] Forget/edit functionality

## Milestone 5 — Dashboard

- [ ] Today
- [ ] This week
- [ ] Tasks
- [ ] Shopping
- [ ] Expenses
- [ ] Events
- [ ] Goals
- [ ] AI insights

## Milestone 6 — Notifications

- [ ] Notification engine
- [ ] Reminder jobs
- [ ] Quiet hours
- [ ] Notification preferences
- [ ] Proactive insights

## Milestone 7 — Planning

- [ ] Plans
- [ ] Trip planning
- [ ] Shared goals
- [ ] Date planning
- [ ] Gift intelligence
- [ ] Commitment tracking

## Milestone 8 — Integrations

- [ ] Google Calendar
- [ ] Telegram
- [ ] Email
- [ ] Receipt OCR
- [ ] CSV imports

## Milestone 9 — Privacy Mode

- [ ] Local LLM support
- [ ] Provider selection
- [ ] Data export
- [ ] Data deletion
- [ ] Encryption improvements
- [ ] Audit tools

---

# 55. Definition of Done for V1

V1 should be considered successful when:

- Two users can create and join a couple.
- Users can submit arbitrary natural-language input.
- The AI can classify common intents.
- The AI can safely call tools.
- Tasks can be created and completed.
- Shopping items can be created and purchased.
- Expenses can be recorded and queried.
- Events/reminders can be created.
- Memories can be stored and retrieved.
- Shared/private visibility works correctly.
- The dashboard summarizes the couple's current state.
- Notifications work reliably.
- Users can correct/delete memories.
- AI actions are auditable.
- AI does not claim failed actions succeeded.
- Private data cannot leak into shared responses.
- The system can be used for normal daily life without requiring users to understand its internal data model.

---

# 56. Important Engineering Constraints

1. Do not build the entire product before testing the core interaction.
2. Do not create unnecessary microservices.
3. Prefer a modular monolith initially.
4. Keep AI providers behind an abstraction.
5. Keep domain logic independent from the LLM.
6. Never make the LLM the source of truth for structured data.
7. Use deterministic application code for calculations.
8. Treat AI output as untrusted input.
9. Validate every tool call.
10. Enforce authorization before retrieval and mutation.
11. Keep private and shared contexts explicitly separated.
12. Log AI actions.
13. Make destructive operations recoverable where possible.
14. Build deletion/export functionality early.
15. Avoid autonomous actions until the underlying workflows are reliable.

---

# 57. Recommended Initial Architecture

Start as a modular monolith:

```text
                    ┌────────────────────┐
                    │      Web App       │
                    │ React / Next.js    │
                    └─────────┬──────────┘
                              │
                              ▼
                    ┌────────────────────┐
                    │    ASP.NET Core    │
                    │       API          │
                    └─────────┬──────────┘
                              │
          ┌───────────────────┼────────────────────┐
          ▼                   ▼                    ▼
   Application Layer      AI Layer           Background Jobs
          │                   │                    │
          │                   ▼                    │
          │              LLM Provider              │
          │                   │                    │
          └───────────────────┼────────────────────┘
                              ▼
                       PostgreSQL
                         + pgvector
```

Keep the first deployment simple.

---

# 58. What Claude Should Do With This Document

Claude should treat this document as the initial product specification and planning context.

Before writing production code, Claude should:

1. Review the requirements.
2. Identify ambiguities and contradictions.
3. Propose the initial architecture.
4. Propose the domain model.
5. Propose database schema.
6. Propose API boundaries.
7. Propose AI/tool architecture.
8. Identify security/privacy risks.
9. Define V0 implementation scope.
10. Create a phased implementation plan.
11. Identify technical decisions that should become ADRs.
12. Ask only high-value clarification questions.
13. Avoid implementing future features prematurely.

The first implementation target should be **V0**, not the entire roadmap.

---

# 59. Claude Planning Instructions

When planning or implementing Couple OS:

### Prioritize

```text
Correctness
Privacy
Maintainability
Simple architecture
Testability
Observability
User trust
```

### Avoid

```text
Premature microservices
Over-engineering
Unnecessary abstractions
Autonomous destructive actions
Storing everything as embeddings
Treating LLM output as truth
Building every feature before validation
```

### Preferred development approach

```text
Plan
  ↓
Architecture decision
  ↓
Small implementation
  ↓
Unit tests
  ↓
Integration tests
  ↓
Manual validation
  ↓
Document
  ↓
Next increment
```

---

# 60. Long-Term Vision

The long-term goal is not simply an app where couples chat with AI.

The goal is:

> **A private, intelligent memory and coordination layer for a couple's everyday life.**

Eventually, Couple OS should be able to understand:

```text
Who we are
What matters to us
What we're planning
What we've decided
What we promised
What we need
What we're spending
What we're working toward
What is coming next
```

And turn that understanding into useful actions.

The ideal interaction should feel less like:

> "Ask an AI a question."

and more like:

> "Tell our household assistant what's happening."

That distinction is the foundation of Couple OS.
