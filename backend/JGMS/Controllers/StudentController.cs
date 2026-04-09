using BLL.DTOs.Admin;
using BLL.DTOs.Jira;
using BLL.Helpers;
using BLL.Services.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;
using System.Security.Claims;
using StudentDTOs = BLL.DTOs.Student;

namespace SWP391_JGMS.Controllers
{
    /// <summary>
    /// Student API — unified controller for all student operations.
    /// Covers personal tasks, statistics, profile, and group-scoped actions.
    ///
    /// Group endpoints accept a group code (e.g. "SE1234") or numeric group ID.
    /// Leader-only endpoints are gated by is_leader checks inside the service layer.
    /// </summary>
    [ApiController]
    [Authorize(Roles = "student,admin,lecturer")]
    [Route("api/students")]
    [Produces("application/json")]
    public class StudentController : ControllerBase
    {
        private readonly IStudentService _studentService;
        private readonly ITeamLeaderService _teamLeaderService;
        private readonly ITeamMemberService _teamMemberService;
        private readonly IdentifierResolver _resolver;

        public StudentController(
            IStudentService studentService,
            ITeamLeaderService teamLeaderService,
            ITeamMemberService teamMemberService,
            IdentifierResolver resolver)
        {
            _studentService = studentService;
            _teamLeaderService = teamLeaderService;
            _teamMemberService = teamMemberService;
            _resolver = resolver;
        }

        /// <summary>Reads the authenticated user's ID from the JWT sub claim.</summary>
        private int GetCurrentUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                           ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (int.TryParse(userIdClaim, out var id)) return id;
            throw new UnauthorizedAccessException("Invalid or missing user identity in token.");
        }

        // ====================================================================
        // My Tasks (all students)
        // ====================================================================

        #region My Tasks

        /// <summary>
        /// Get all tasks assigned to the current student.
        /// </summary>
        [HttpGet("tasks")]
        [ProducesResponseType(typeof(List<StudentDTOs.TaskResponseDTO>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetMyTasks()
        {
            try
            {
                var tasks = await _studentService.GetMyTasksAsync(GetCurrentUserId());
                return Ok(tasks);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Get a specific task by ID (must be assigned to you).
        /// </summary>
        [HttpGet("tasks/{taskId}")]
        [ProducesResponseType(typeof(StudentDTOs.TaskResponseDTO), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetTaskById(int taskId)
        {
            try
            {
                var task = await _studentService.GetTaskByIdAsync(taskId, GetCurrentUserId());
                if (task == null)
                    return NotFound(new { message = "Task not found" });
                return Ok(task);
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Generate an auto commit line suggestion from either task ID or Jira issue key.
        /// Supports optional conventional-commit customization and returns Git/GitHub commands.
        /// </summary>
        [HttpPost("commit-line")]
        [ProducesResponseType(typeof(StudentDTOs.CommitLineSuggestionResponseDTO), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GenerateCommitLine([FromBody] StudentDTOs.CommitLineSuggestionRequestDTO dto)
        {
            try
            {
                if (dto == null)
                    return BadRequest(new { message = "Request body is required" });

                var suggestion = await _studentService.GenerateCommitLineSuggestionAsync(GetCurrentUserId(), dto);
                return Ok(suggestion);
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, new { message = ex.Message });
            }
            catch (Exception ex)
            {
                if (ex.Message.Equals("Task not found", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Equals("Jira issue not found", StringComparison.OrdinalIgnoreCase))
                    return NotFound(new { message = ex.Message });

                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Update the status of a task assigned to you.
        /// Accepted status values (case-insensitive, flexible formatting):
        /// - "todo", "To Do", "to do", "to_do"
        /// - "in_progress", "In Progress", "in progress", "in-progress"
        /// - "done", "Done"
        /// </summary>
        [HttpPut("tasks/{taskId}/status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> UpdateTaskStatus(int taskId, [FromBody] StudentDTOs.UpdateTaskStatusDTO dto)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                await _studentService.UpdateTaskStatusAsync(taskId, GetCurrentUserId(), dto);
                return Ok(new { message = "Task status updated successfully" });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Forbid(ex.Message);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Mark a task as completed — sets CompletedAt timestamp.
        /// Only works for tasks assigned to you.
        /// </summary>
        [HttpPost("tasks/{taskId}/complete")]
        [ProducesResponseType(typeof(TaskResponseDTO), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> CompleteTask(int taskId)
        {
            try
            {
                var task = await _teamMemberService.CompleteTaskAsync(GetCurrentUserId(), taskId);
                return Ok(task);
            }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Access denied"))
                    return StatusCode(403, new { message = ex.Message });
                return BadRequest(new { message = ex.Message });
            }
        }

        #endregion

        // ====================================================================
        // Statistics (all students)
        // ====================================================================

        #region Statistics

        /// <summary>
        /// Get personal task and commit statistics overview.
        /// Shows completed vs pending tasks, commit history, contribution metrics.
        /// </summary>
        [HttpGet("statistics")]
        [ProducesResponseType(typeof(StudentDTOs.PersonalStatisticsDTO), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetPersonalStatistics([FromQuery] int? projectId = null)
        {
            try
            {
                var statistics = await _studentService.GetPersonalStatisticsAsync(GetCurrentUserId(), projectId);
                return Ok(statistics);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Get task statistics grouped by status (todo, in_progress, done).
        /// </summary>
        [HttpGet("statistics/tasks-by-status")]
        [ProducesResponseType(typeof(StudentDTOs.TaskStatisticsByStatusDTO), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetTaskStatisticsByStatus()
        {
            try
            {
                var statistics = await _studentService.GetTaskStatisticsByStatusAsync(GetCurrentUserId());
                return Ok(statistics);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Get personal task statistics (completion and progress metrics).
        /// </summary>
        [HttpGet("statistics/tasks")]
        [ProducesResponseType(typeof(PersonalTaskStatisticResponseDTO), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetMyTaskStatistics()
        {
            try
            {
                var statistics = await _teamMemberService.GetMyTaskStatisticsAsync(GetCurrentUserId());
                if (statistics == null)
                    return NotFound(new { message = "Task statistics not found" });
                return Ok(statistics);
            }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Get personal commit statistics (commits this week/month, averages).
        /// </summary>
        [HttpGet("statistics/commits")]
        [ProducesResponseType(typeof(CommitStatisticResponseDTO), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetMyCommitStatistics()
        {
            try
            {
                var statistics = await _teamMemberService.GetMyCommitStatisticsAsync(GetCurrentUserId());
                if (statistics == null)
                    return NotFound(new { message = "Commit statistics not found" });
                return Ok(statistics);
            }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Get personal commit history timeline from GitHub.
        /// </summary>
        [HttpGet("commits")]
        [ProducesResponseType(typeof(List<StudentDTOs.CommitHistoryDTO>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetCommitHistory([FromQuery] int? projectId = null)
        {
            try
            {
                var commits = await _studentService.GetCommitHistoryAsync(GetCurrentUserId(), projectId);
                return Ok(commits);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        #endregion

        // ====================================================================
        // Profile (all students)
        // ====================================================================

        #region Profile

        /// <summary>
        /// Get current student's profile information.
        /// </summary>
        [HttpGet("profile")]
        [ProducesResponseType(typeof(UserResponseDTO), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetMyProfile()
        {
            try
            {
                var profile = await _studentService.GetMyProfileAsync(GetCurrentUserId());
                return Ok(profile);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Update basic profile information (phone, GitHub username, Jira account).
        /// Students can only update specific fields, not role or status.
        /// </summary>
        [HttpPut("profile")]
        [ProducesResponseType(typeof(UserResponseDTO), StatusCodes.Status200OK)]
        public async Task<IActionResult> UpdateMyProfile([FromBody] StudentDTOs.UpdateProfileDTO dto)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var profile = await _studentService.UpdateMyProfileAsync(GetCurrentUserId(), dto);
                return Ok(profile);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        #endregion

        // ====================================================================
        // SRS Documents — Personal Access (all students)
        // Accepts group code (e.g. "SE1234") or group ID to identify project.
        // ====================================================================

        #region SRS Documents (Personal)

        /// <summary>
        /// Get SRS documents for a project, identified by group code (e.g. "SE1234") or group ID.
        /// Any group member can access. For leader-only SRS management, see the Group SRS Documents section.
        /// </summary>
        [HttpGet("my-srs-documents/{groupCode}")]
        [ProducesResponseType(typeof(List<StudentDTOs.SrsDocumentDTO>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetSrsDocumentsByProject(string groupCode)
        {
            try
            {
                var projectId = await _resolver.ResolveProjectIdAsync(groupCode);
                var documents = await _studentService.GetSrsDocumentsByProjectAsync(projectId, GetCurrentUserId());
                return Ok(documents);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Get a specific SRS document by ID.
        /// </summary>
        [HttpGet("srs-documents/{documentId}")]
        [ProducesResponseType(typeof(StudentDTOs.SrsDocumentDTO), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetSrsDocumentById(int documentId)
        {
            try
            {
                var document = await _studentService.GetSrsDocumentByIdAsync(documentId, GetCurrentUserId());
                if (document == null)
                    return NotFound(new { message = "SRS document not found" });
                return Ok(document);
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Download SRS document file.
        /// Security: Validates file path to prevent directory traversal attacks.
        /// </summary>
        [HttpGet("srs-documents/{documentId}/download")]
        public async Task<IActionResult> DownloadSrsDocument(int documentId)
        {
            try
            {
                var document = await _studentService.GetSrsDocumentByIdAsync(documentId, GetCurrentUserId());
                if (document == null)
                    return NotFound(new { message = "SRS document not found" });

                if (string.IsNullOrEmpty(document.FilePath))
                    return NotFound(new { message = "Document file path not configured" });

                var baseDirectory = Path.Combine(Directory.GetCurrentDirectory(), "SrsDocuments");
                var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, document.FilePath));

                var normalizedBaseDirectory = Path.GetFullPath(baseDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(normalizedBaseDirectory, StringComparison.OrdinalIgnoreCase))
                    return BadRequest(new { message = "Invalid file path" });

                if (!System.IO.File.Exists(fullPath))
                    return NotFound(new { message = "Document file not found on server" });

                var fileBytes = await System.IO.File.ReadAllBytesAsync(fullPath);
                var fileName = $"{document.DocumentTitle}_v{document.Version}.pdf";
                return File(fileBytes, "application/pdf", fileName);
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        #endregion

        // ====================================================================
        // My Group (all students)
        // ====================================================================

        #region My Group

        /// <summary>
        /// Get the group the current student belongs to.
        /// Returns 404 if the student has not been assigned to a group yet.
        /// Includes group details, project info, and the list of team members.
        /// </summary>
        [HttpGet("my-group")]
        [ProducesResponseType(typeof(StudentDTOs.MyGroupDTO), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetMyGroup()
        {
            try
            {
                var group = await _studentService.GetMyGroupAsync(GetCurrentUserId());
                if (group == null)
                    return NotFound(new { message = "You are not assigned to any group yet." });
                return Ok(group);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        #endregion

        // ====================================================================
        // Group Project & Requirements (leader-gated by service layer)
        // Accepts group code (e.g. "SE1234") or numeric group ID.
        // ====================================================================

        #region Group Project & Requirements

        /// <summary>
        /// Get project details for a group. Leader only.
        /// Accepts group code (e.g. "SE1234") or numeric group ID.
        /// </summary>
        [HttpGet("groups/{groupCode}/project")]
        [ProducesResponseType(typeof(ProjectResponseDTO), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetGroupProject(string groupCode)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                var project = await _teamLeaderService.GetGroupProjectAsync(GetCurrentUserId(), groupId);
                if (project == null)
                    return NotFound(new { message = "Project not found" });
                return Ok(project);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Access denied")) return StatusCode(403, new { message = ex.Message });
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Get all requirements for a group project.
        /// Any group member can view; only leaders can create/edit/delete.
        /// Accepts group code (e.g. "SE1234") or numeric group ID.
        /// </summary>
        [HttpGet("groups/{groupCode}/requirements")]
        [ProducesResponseType(typeof(List<RequirementResponseDTO>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> GetGroupRequirements(string groupCode)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                var requirements = await _teamMemberService.GetGroupRequirementsAsync(GetCurrentUserId(), groupId);
                return Ok(requirements);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Access denied"))
                    return StatusCode(403, new { message = ex.Message });
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Create a requirement and push it to Jira as a new issue. Leader only.
        /// Accepts group code (e.g. "SE1234") or numeric group ID.
        /// </summary>
        [HttpPost("groups/{groupCode}/requirements")]
        [ProducesResponseType(typeof(RequirementResponseDTO), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> CreateRequirement(string groupCode, [FromBody] CreateRequirementDTO dto)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                var requirement = await _teamLeaderService.CreateRequirementAsync(GetCurrentUserId(), groupId, dto);
                return CreatedAtAction(nameof(GetGroupRequirements), new { groupCode }, requirement);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Update a requirement and sync changes back to Jira. Leader only.
        /// Accepts group code or group ID, and requirement ID.
        /// </summary>
        [HttpPut("groups/{groupCode}/requirements/{requirementId}")]
        [ProducesResponseType(typeof(RequirementResponseDTO), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> UpdateRequirement(string groupCode, int requirementId, [FromBody] UpdateRequirementDTO dto)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                var requirement = await _teamLeaderService.UpdateRequirementAsync(GetCurrentUserId(), groupId, requirementId, dto);
                return Ok(requirement);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Delete a requirement and remove the corresponding issue from Jira. Leader only.
        /// </summary>
        [HttpDelete("groups/{groupCode}/requirements/{requirementId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> DeleteRequirement(string groupCode, int requirementId)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                await _teamLeaderService.DeleteRequirementAsync(GetCurrentUserId(), groupId, requirementId);
                return Ok(new { message = "Requirement deleted successfully" });
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Reorder requirements hierarchy (Epic → Story → Task → Sub-task). Leader only.
        /// </summary>
        [HttpPut("groups/{groupCode}/requirements/reorder")]
        [ProducesResponseType(typeof(List<RequirementResponseDTO>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> ReorderRequirements(string groupCode, [FromBody] ReorderRequirementsDTO dto)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                var ordered = await _teamLeaderService.ReorderRequirementsAsync(GetCurrentUserId(), groupId, dto);
                return Ok(ordered);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Bulk-import all synced Jira issues as requirements. Leader only.
        /// Skips issues that are already linked to an existing requirement.
        /// Run a Jira sync first to ensure issues are up to date.
        /// </summary>
        [HttpPost("groups/{groupCode}/requirements/import-from-jira")]
        [ProducesResponseType(typeof(BulkImportFromJiraResultDTO), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> ImportRequirementsFromJira(string groupCode)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                var result = await _teamLeaderService.ImportRequirementsFromJiraAsync(GetCurrentUserId(), groupId);
                return Ok(result);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        #endregion

        // ====================================================================
        // Group Tasks & Jira Sync (leader-gated by service layer)
        // ====================================================================

        #region Group Tasks & Jira Sync

        /// <summary>
        /// Get all tasks for a group project. Leader only.
        /// Accepts group code (e.g. "SE1234") or numeric group ID.
        /// </summary>
        [HttpGet("groups/{groupCode}/tasks")]
        [ProducesResponseType(typeof(List<TaskResponseDTO>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> GetGroupTasks(string groupCode)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                var tasks = await _teamLeaderService.GetGroupTasksAsync(GetCurrentUserId(), groupId);
                return Ok(tasks);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Create a task, assign a member, set deadline and optionally link to Jira. Leader only.
        /// </summary>
        [HttpPost("groups/{groupCode}/tasks")]
        [ProducesResponseType(typeof(TaskResponseDTO), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> CreateTask(string groupCode, [FromBody] CreateTaskDTO dto)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                var task = await _teamLeaderService.CreateTaskAsync(GetCurrentUserId(), groupId, dto);
                return CreatedAtAction(nameof(GetGroupTasks), new { groupCode }, task);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Create a task pre-populated from a synced Jira issue key (e.g. "SWP391-5"). Leader only.
        /// </summary>
        [HttpPost("groups/{groupCode}/tasks/from-jira")]
        [ProducesResponseType(typeof(TaskResponseDTO), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> CreateTaskFromJiraIssue(string groupCode, [FromBody] CreateTaskFromJiraIssueDTO dto)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                var task = await _teamLeaderService.CreateTaskFromJiraIssueAsync(GetCurrentUserId(), groupId, dto);
                return CreatedAtAction(nameof(GetGroupTasks), new { groupCode }, task);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Edit a task: update status, assignee, description, priority, due date. Leader only.
        /// Changes are synced back to Jira if the task is linked to a Jira issue.
        /// </summary>
        [HttpPut("groups/{groupCode}/tasks/{taskId}")]
        [ProducesResponseType(typeof(TaskResponseDTO), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> UpdateTask(string groupCode, int taskId, [FromBody] UpdateTaskDTO dto)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                var task = await _teamLeaderService.UpdateTaskAsync(GetCurrentUserId(), groupId, taskId, dto);
                return Ok(task);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Assign a task to a group member. Leader only.
        /// </summary>
        [HttpPost("groups/{groupCode}/tasks/{taskId}/assign")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> AssignTask(string groupCode, int taskId, [FromBody] AssignTaskDTO dto)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                await _teamLeaderService.AssignTaskAsync(GetCurrentUserId(), groupId, taskId, dto.MemberId);
                return Ok(new { message = "Task assigned successfully" });
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Delete a task. Also removes the linked Jira issue if integration is configured. Leader only.
        /// </summary>
        [HttpDelete("groups/{groupCode}/tasks/{taskId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> DeleteTask(string groupCode, int taskId)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                await _teamLeaderService.DeleteTaskAsync(GetCurrentUserId(), groupId, taskId);
                return Ok(new { message = "Task deleted successfully" });
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Move a task's linked Jira issue to a different sprint. Leader only.
        /// Use sprintId = 0 to move the issue back to the backlog.
        /// </summary>
        [HttpPut("groups/{groupCode}/tasks/{taskId}/sprint")]
        [ProducesResponseType(typeof(TaskResponseDTO), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> MoveTaskToSprint(string groupCode, int taskId, [FromBody] MoveTaskToSprintDTO dto)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                var task = await _teamLeaderService.MoveTaskToSprintAsync(GetCurrentUserId(), groupId, taskId, dto.SprintId);
                return Ok(task);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Link a task to a requirement. Creates a "Relates" issue link in Jira if applicable. Leader only.
        /// </summary>
        [HttpPut("groups/{groupCode}/tasks/{taskId}/requirement")]
        [ProducesResponseType(typeof(TaskResponseDTO), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> LinkTaskToRequirement(string groupCode, int taskId, [FromBody] LinkTaskToRequirementDTO dto)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                var task = await _teamLeaderService.LinkTaskToRequirementAsync(GetCurrentUserId(), groupId, taskId, dto.RequirementId);
                return Ok(task);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Push all local changes (tasks + requirements) to Jira in bulk. Leader only.
        /// </summary>
        [HttpPost("groups/{groupCode}/sync-to-jira")]
        [ProducesResponseType(typeof(JiraPushSyncResultDTO), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> SyncToJira(string groupCode)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                var result = await _teamLeaderService.SyncToJiraAsync(GetCurrentUserId(), groupId);
                return Ok(result);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        #endregion

        // ====================================================================
        // Group SRS Documents (leader-gated by service layer)
        // ====================================================================

        #region Group SRS Documents

        /// <summary>
        /// Get all SRS documents for a group's project. Leader only.
        /// Accepts group code (e.g. "SE1234") or numeric group ID.
        /// </summary>
        [HttpGet("groups/{groupCode}/srs-documents")]
        [ProducesResponseType(typeof(List<SrsDocumentResponseDTO>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> GetGroupSrsDocuments(string groupCode)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                var documents = await _teamLeaderService.GetGroupSrsDocumentsAsync(GetCurrentUserId(), groupId);
                return Ok(documents);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Get a single SRS document by ID with all included requirements. Leader only.
        /// </summary>
        [HttpGet("groups/{groupCode}/srs-documents/{documentId}")]
        [ProducesResponseType(typeof(SrsDocumentResponseDTO), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> GetGroupSrsDocument(string groupCode, int documentId)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                var srs = await _teamLeaderService.GetGroupSrsDocumentAsync(GetCurrentUserId(), groupId, documentId);
                if (srs == null)
                    return NotFound(new { message = "SRS document not found" });
                return Ok(srs);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Generate an SRS document from existing requirements. Leader only.
        /// </summary>
        [HttpPost("groups/{groupCode}/srs-documents/generate")]
        [ProducesResponseType(typeof(SrsDocumentResponseDTO), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> GenerateSrsDocument(string groupCode, [FromBody] CreateSrsDocumentDTO dto)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                var srs = await _teamLeaderService.GenerateSrsDocumentAsync(GetCurrentUserId(), groupId, dto);
                return CreatedAtAction(nameof(GetGroupSrsDocument), new { groupCode, documentId = srs.DocumentId }, srs);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Update SRS document metadata. Leader only.
        /// Set status to "published" to finalize the document.
        /// </summary>
        [HttpPut("groups/{groupCode}/srs-documents/{documentId}")]
        [ProducesResponseType(typeof(SrsDocumentResponseDTO), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> UpdateSrsDocument(string groupCode, int documentId, [FromBody] UpdateSrsDocumentDTO dto)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                var srs = await _teamLeaderService.UpdateSrsDocumentAsync(GetCurrentUserId(), groupId, documentId, dto);
                return Ok(srs);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Download the SRS document as an HTML file. Leader only.
        /// </summary>
        [HttpGet("groups/{groupCode}/srs-documents/{documentId}/download")]
        [Produces("text/html")]
        [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> DownloadGroupSrsDocument(string groupCode, int documentId)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                var (content, fileName) = await _teamLeaderService.DownloadSrsDocumentAsync(GetCurrentUserId(), groupId, documentId);
                return File(content, "text/html", fileName);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Download the SRS document as a Word-compatible (.doc) file. Leader only.
        /// </summary>
        [HttpGet("groups/{groupCode}/srs-documents/{documentId}/download/doc")]
        [Produces("application/msword")]
        [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> DownloadGroupSrsDocumentAsDoc(string groupCode, int documentId)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                var (content, fileName) = await _teamLeaderService.DownloadSrsDocumentAsDocAsync(GetCurrentUserId(), groupId, documentId);
                return File(content, "application/msword", fileName);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        /// <summary>
        /// Regenerate the requirements snapshot of an existing SRS document. Leader only.
        /// Replaces all previously included requirements with a fresh selection.
        /// Optionally auto-imports new Jira issues as requirements before regenerating.
        /// Does NOT create a new SRS version — use the generate endpoint for that.
        /// </summary>
        [HttpPost("groups/{groupCode}/srs-documents/{documentId}/regenerate")]
        [ProducesResponseType(typeof(SrsDocumentResponseDTO), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> RegenerateSrsDocument(string groupCode, int documentId, [FromBody] RegenerateSrsDocumentDTO dto)
        {
            try
            {
                var groupId = await _resolver.ResolveGroupIdAsync(groupCode);
                var srs = await _teamLeaderService.RegenerateSrsDocumentAsync(GetCurrentUserId(), groupId, documentId, dto);
                return Ok(srs);
            }
            catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
            catch (Exception ex) { return ex.Message.Contains("Access denied") ? Forbid() : BadRequest(new { message = ex.Message }); }
        }

        #endregion
    }
}
