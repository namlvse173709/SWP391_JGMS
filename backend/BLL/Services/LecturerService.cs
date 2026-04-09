using BLL.DTOs.Admin;
using BLL.Services.Interface;
using DAL.Models;
using DAL.Repositories.Interface;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Task = System.Threading.Tasks.Task;

namespace BLL.Services
{
    public class LecturerService : ILecturerService
    {
        private readonly IStudentGroupRepository _groupRepository;
        private readonly IUserRepository _userRepository;
        private readonly IGroupMemberRepository _memberRepository;
        private readonly IRequirementRepository _requirementRepository;
        private readonly ITaskRepository _taskRepository;
        private readonly IProjectRepository _projectRepository;
        private readonly ICommitRepository _commitRepository;
        private readonly IJiraIntegrationRepository _jiraIntegrationRepository;
        private readonly IGithubIntegrationRepository _githubIntegrationRepository;
        private readonly IJiraIssueRepository _jiraIssueRepository;
        private readonly IGithubCommitRepository _githubCommitRepository;

        public LecturerService(
            IStudentGroupRepository groupRepository,
            IUserRepository userRepository,
            IGroupMemberRepository memberRepository,
            IRequirementRepository requirementRepository,
            ITaskRepository taskRepository,
            IProjectRepository projectRepository,
            ICommitRepository commitRepository,
            IJiraIntegrationRepository jiraIntegrationRepository,
            IGithubIntegrationRepository githubIntegrationRepository,
            IJiraIssueRepository jiraIssueRepository,
            IGithubCommitRepository githubCommitRepository)
        {
            _groupRepository = groupRepository;
            _userRepository = userRepository;
            _memberRepository = memberRepository;
            _requirementRepository = requirementRepository;
            _taskRepository = taskRepository;
            _projectRepository = projectRepository;
            _commitRepository = commitRepository;
            _jiraIntegrationRepository = jiraIntegrationRepository;
            _githubIntegrationRepository = githubIntegrationRepository;
            _jiraIssueRepository = jiraIssueRepository;
            _githubCommitRepository = githubCommitRepository;
        }

        private async Task CheckLecturerAssignmentAsync(int userId, StudentGroup group)
        {
            var user = await _userRepository.GetByIdAsync(userId);
            if (user != null && user.Role == UserRole.admin) return; // Admins can access everything

            if (group.LecturerId != userId)
            {
                throw new Exception("Access denied. You are not assigned to this group.");
            }
        }

        public async Task<StudentGroupResponseDTO?> GetGroupByIdAsync(int lecturerId, int groupId)
        {
            var group = await _groupRepository.GetByIdAsync(groupId);
            if (group == null)
            {
                throw new Exception("Student group not found");
            }

            await CheckLecturerAssignmentAsync(lecturerId, group);

            var groupDetails = await _groupRepository.GetGroupWithDetailsAsync(groupId);
            if (groupDetails == null) return null;

            var dto = MapToGroupResponse(groupDetails);

            var project = await _projectRepository.GetByGroupIdAsync(groupId);
            if (project != null)
            {
                dto.Project = await MapToProjectResponseAsync(project);
            }

            return dto;
        }

        public async Task<List<StudentGroupResponseDTO>> GetMyGroupsAsync(int lecturerId)
        {
            var lecturer = await _userRepository.GetByIdAsync(lecturerId);
            if (lecturer == null || (lecturer.Role != UserRole.lecturer && lecturer.Role != UserRole.admin))
            {
                throw new Exception("User not found or invalid role for this operation");
            }

            var groups = await _groupRepository.GetByLecturerIdAsync(lecturerId);
            var dtos = new List<StudentGroupResponseDTO>();

            foreach (var group in groups)
            {
                var dto = MapToGroupResponse(group);
                var project = await _projectRepository.GetByGroupIdAsync(group.GroupId);
                if (project != null)
                {
                    dto.Project = await MapToProjectResponseAsync(project);
                }
                dtos.Add(dto);
            }

            return dtos;
        }

        public async Task<List<GroupMemberResponseDTO>> GetGroupMembersAsync(int lecturerId, int groupId)
        {
            var group = await _groupRepository.GetByIdAsync(groupId);
            if (group == null)
            {
                throw new Exception("Student group not found");
            }

            await CheckLecturerAssignmentAsync(lecturerId, group);

            var members = await _memberRepository.GetByGroupIdAsync(groupId);
            return members.Select(m => new GroupMemberResponseDTO
            {
                MemberId = m.MembershipId,
                GroupId = m.GroupId,
                UserId = m.UserId,
                UserName = m.User.FullName,
                Email = m.User.Email,
                IsLeader = m.IsLeader.GetValueOrDefault(false),
                JoinedAt = m.JoinedAt,
                LeftAt = m.LeftAt
            }).ToList();
        }

        public async Task<BulkAddResult> AddStudentsToGroupAsync(int lecturerId, int groupId, List<string> studentIdentifiers)
        {
            var group = await _groupRepository.GetByIdAsync(groupId);
            if (group == null)
                throw new Exception("Student group not found");

            await CheckLecturerAssignmentAsync(lecturerId, group);

            var result = new BulkAddResult();

            foreach (var identifier in studentIdentifiers)
            {
                try
                {
                    // Resolve identifier → student ID
                    User? student = null;
                    if (int.TryParse(identifier, out var numId))
                        student = await _userRepository.GetByIdAsync(numId);
                    else
                        student = await _userRepository.GetByEmailAsync(identifier);

                    if (student == null)
                        throw new Exception($"User '{identifier}' not found.");
                    if (student.Role != UserRole.student)
                        throw new Exception($"'{identifier}' is not a student account.");

                    if (await _memberRepository.IsMemberOfGroupAsync(groupId, student.UserId))
                        throw new Exception("Already a member of this group.");

                    if (await _memberRepository.IsStudentInAnyGroupAsync(student.UserId))
                        throw new Exception("Already a member of another group.");

                    var previous = await _memberRepository.GetPreviousMembershipAsync(groupId, student.UserId);
                    if (previous != null)
                        await _memberRepository.RejoinAsync(previous);
                    else
                        await _memberRepository.AddAsync(new GroupMember
                        {
                            GroupId = groupId,
                            UserId = student.UserId,
                            IsLeader = false,
                            JoinedAt = DateTime.UtcNow
                        });

                    result.Added.Add(identifier);
                    result.SuccessCount++;
                }
                catch (Exception ex)
                {
                    result.Failures.Add(new BulkAddFailure { Identifier = identifier, Reason = ex.Message });
                    result.FailureCount++;
                }
            }

            return result;
        }

        public async System.Threading.Tasks.Task RemoveStudentFromGroupAsync(int lecturerId, int groupId, string studentIdentifier)
        {
            var group = await _groupRepository.GetByIdAsync(groupId);
            if (group == null)
                throw new Exception("Student group not found");

            await CheckLecturerAssignmentAsync(lecturerId, group);

            User? student = null;
            if (int.TryParse(studentIdentifier, out var numId))
                student = await _userRepository.GetByIdAsync(numId);
            else
                student = await _userRepository.GetByEmailAsync(studentIdentifier);

            if (student == null)
                throw new KeyNotFoundException($"Student '{studentIdentifier}' not found.");

            if (!await _memberRepository.IsMemberOfGroupAsync(groupId, student.UserId))
                throw new Exception("Student is not a member of this group");

            if (group.LeaderId == student.UserId)
                throw new Exception("Cannot remove the group leader. Assign a different leader first before removing this student.");

            await _memberRepository.RemoveAsync(groupId, student.UserId);
        }

        public async Task<StudentGroupResponseDTO> UpdateGroupAsync(int lecturerId, int groupId, UpdateStudentGroupDTO dto)
        {
            var group = await _groupRepository.GetByIdAsync(groupId);
            if (group == null)
            {
                throw new Exception("Student group not found");
            }

            await CheckLecturerAssignmentAsync(lecturerId, group);

            if (!string.IsNullOrEmpty(dto.GroupName))
                group.GroupName = dto.GroupName;

            if (dto.Status.HasValue)
                group.Status = dto.Status.Value;

            group.UpdatedAt = DateTime.UtcNow;
            await _groupRepository.UpdateAsync(group);

            var updatedGroup = await _groupRepository.GetGroupWithDetailsAsync(groupId);
            var resDto = MapToGroupResponse(updatedGroup!);

            var project = await _projectRepository.GetByGroupIdAsync(groupId);
            if (project != null)
            {
                resDto.Project = await MapToProjectResponseAsync(project);
            }

            return resDto;
        }

        private async Task<ProjectResponseDTO> MapToProjectResponseAsync(Project project)
        {
            var dto = new ProjectResponseDTO
            {
                ProjectId = project.ProjectId,
                GroupId = project.GroupId,
                GroupCode = project.Group?.GroupCode,
                GroupName = project.Group?.GroupName,
                ProjectName = project.ProjectName,
                Description = project.Description,
                StartDate = project.StartDate,
                EndDate = project.EndDate,
                Status = project.Status?.ToString(),
                CreatedAt = project.CreatedAt,
                UpdatedAt = project.UpdatedAt
            };

            // Enhanced with integration status
            var jira = await _jiraIntegrationRepository.GetByProjectIdAsync(project.ProjectId);
            if (jira != null)
            {
                dto.JiraStatus = new ProjectIntegrationStatusDTO
                {
                    IsConfigured = true,
                    SyncStatus = jira.SyncStatus.ToString(),
                    LastSync = jira.LastSync,
                    TotalItems = await _jiraIssueRepository.GetCountByProjectIdAsync(project.ProjectId)
                };
            }

            var github = await _githubIntegrationRepository.GetByProjectIdAsync(project.ProjectId);
            if (github != null)
            {
                dto.GithubStatus = new ProjectIntegrationStatusDTO
                {
                    IsConfigured = true,
                    SyncStatus = github.SyncStatus.ToString(),
                    LastSync = github.LastSync,
                    TotalItems = await _githubCommitRepository.GetCountByProjectIdAsync(project.ProjectId)
                };
            }

            return dto;
        }

        public async Task<List<RequirementResponseDTO>> GetGroupRequirementsAsync(int lecturerId, int groupId)
        {
            var group = await _groupRepository.GetByIdAsync(groupId)
                ?? throw new Exception("Student group not found");

            await CheckLecturerAssignmentAsync(lecturerId, group);

            var project = await _projectRepository.GetByGroupIdAsync(groupId);
            if (project == null)
                return new List<RequirementResponseDTO>();

            var requirements = await _requirementRepository.GetByProjectIdAsync(project.ProjectId);
            return requirements.Select(MapToRequirementResponse).ToList();
        }

        public async Task<List<TaskResponseDTO>> GetGroupTasksAsync(int lecturerId, int groupId)
        {
            var group = await _groupRepository.GetByIdAsync(groupId)
                ?? throw new Exception("Student group not found");

            await CheckLecturerAssignmentAsync(lecturerId, group);

            var project = await _projectRepository.GetByGroupIdAsync(groupId);
            if (project == null)
                return new List<TaskResponseDTO>();

            var tasks = await _taskRepository.GetTasksByProjectIdAsync(project.ProjectId);
            return tasks.Select(MapToTaskResponse).ToList();
        }

        public async Task<List<ProgressReportResponseDTO>> GetProjectProgressReportsAsync(int lecturerId, int groupId)
        {
            var group = await _groupRepository.GetByIdAsync(groupId)
                ?? throw new Exception("Student group not found");

            await CheckLecturerAssignmentAsync(lecturerId, group);

            var project = await _projectRepository.GetByGroupIdAsync(groupId);
            if (project == null)
                return new List<ProgressReportResponseDTO>();

            var reports = await _projectRepository.GetProgressReportsByProjectIdAsync(project.ProjectId);
            return reports.Select(r => new ProgressReportResponseDTO
            {
                ReportId = r.ReportId,
                ProjectId = r.ProjectId,
                ReportPeriodStart = r.ReportPeriodStart,
                ReportPeriodEnd = r.ReportPeriodEnd,
                ReportData = r.ReportData,
                Summary = r.Summary,
                FilePath = r.FilePath,
                GeneratedBy = r.GeneratedBy,
                GeneratedByName = r.GeneratedByNavigation?.FullName,
                GeneratedAt = r.GeneratedAt,
                CreatedAt = r.CreatedAt
            }).ToList();
        }

        public async Task<GroupCommitStatisticsResponseDTO> GetGithubCommitStatisticsAsync(int lecturerId, int groupId)
        {
            var group = await _groupRepository.GetByIdAsync(groupId)
                ?? throw new Exception("Student group not found");

            await CheckLecturerAssignmentAsync(lecturerId, group);

            var project = await _projectRepository.GetByGroupIdAsync(groupId);
            if (project == null)
            {
                return new GroupCommitStatisticsResponseDTO
                {
                    GroupId = groupId,
                    GroupCode = group.GroupCode
                };
            }

            var statsRows = await _projectRepository.GetCommitStatisticsByProjectIdAsync(project.ProjectId);
            if (statsRows.Any())
            {
                var latestByUser = statsRows
                    .GroupBy(s => s.UserId)
                    .Select(g => g
                        .OrderByDescending(x => x.PeriodEnd)
                        .ThenByDescending(x => x.UpdatedAt)
                        .First())
                    .ToList();

                return new GroupCommitStatisticsResponseDTO
                {
                    GroupId = groupId,
                    GroupCode = group.GroupCode,
                    ProjectId = project.ProjectId,
                    PeriodStart = latestByUser.Min(s => s.PeriodStart),
                    PeriodEnd = latestByUser.Max(s => s.PeriodEnd),
                    TotalCommits = latestByUser.Sum(s => s.TotalCommits ?? 0),
                    TotalAdditions = latestByUser.Sum(s => s.TotalAdditions ?? 0),
                    TotalDeletions = latestByUser.Sum(s => s.TotalDeletions ?? 0),
                    TotalChangedFiles = latestByUser.Sum(s => s.TotalChangedFiles ?? 0),
                    Members = latestByUser
                        .OrderByDescending(s => s.TotalCommits ?? 0)
                        .Select(s => new MemberCommitStatisticsDTO
                        {
                            UserId = s.UserId,
                            UserName = s.User?.FullName ?? $"User {s.UserId}",
                            TotalCommits = s.TotalCommits ?? 0,
                            TotalAdditions = s.TotalAdditions ?? 0,
                            TotalDeletions = s.TotalDeletions ?? 0,
                            TotalChangedFiles = s.TotalChangedFiles ?? 0,
                            CommitFrequency = s.CommitFrequency,
                            AvgCommitSize = s.AvgCommitSize,
                            LastCommitDate = null
                        })
                        .ToList()
                };
            }

            var commits = await _commitRepository.GetCommitsByProjectIdAsync(project.ProjectId);
            if (!commits.Any())
            {
                return new GroupCommitStatisticsResponseDTO
                {
                    GroupId = groupId,
                    GroupCode = group.GroupCode,
                    ProjectId = project.ProjectId
                };
            }

            var byUser = commits.GroupBy(c => c.UserId).ToList();
            var periodStart = DateOnly.FromDateTime(commits.Min(c => c.CommitDate).Date);
            var periodEnd = DateOnly.FromDateTime(commits.Max(c => c.CommitDate).Date);

            var memberStats = byUser
                .Select(g =>
                {
                    var userCommits = g.ToList();
                    var firstDate = userCommits.Min(c => c.CommitDate).Date;
                    var lastDate = userCommits.Max(c => c.CommitDate).Date;
                    var days = Math.Max(1, (lastDate - firstDate).TotalDays + 1);

                    return new MemberCommitStatisticsDTO
                    {
                        UserId = g.Key,
                        UserName = userCommits.First().User?.FullName ?? $"User {g.Key}",
                        TotalCommits = userCommits.Count,
                        TotalAdditions = userCommits.Sum(c => c.Additions ?? 0),
                        TotalDeletions = userCommits.Sum(c => c.Deletions ?? 0),
                        TotalChangedFiles = userCommits.Sum(c => c.ChangedFiles ?? 0),
                        CommitFrequency = Math.Round((decimal)userCommits.Count / (decimal)days, 2),
                        AvgCommitSize = userCommits.Any()
                            ? (int)Math.Round(userCommits.Average(c => (c.Additions ?? 0) + (c.Deletions ?? 0)))
                            : 0,
                        LastCommitDate = userCommits.Max(c => c.CommitDate)
                    };
                })
                .OrderByDescending(m => m.TotalCommits)
                .ToList();

            return new GroupCommitStatisticsResponseDTO
            {
                GroupId = groupId,
                GroupCode = group.GroupCode,
                ProjectId = project.ProjectId,
                PeriodStart = periodStart,
                PeriodEnd = periodEnd,
                TotalCommits = commits.Count,
                TotalAdditions = commits.Sum(c => c.Additions ?? 0),
                TotalDeletions = commits.Sum(c => c.Deletions ?? 0),
                TotalChangedFiles = commits.Sum(c => c.ChangedFiles ?? 0),
                Members = memberStats
            };
        }

        private StudentGroupResponseDTO MapToGroupResponse(StudentGroup group)
        {
            var activeMembers = group.GroupMembers
                .Where(m => m.LeftAt == null)
                .OrderByDescending(m => m.IsLeader)
                .ThenBy(m => m.User.FullName)
                .Select(m => new GroupMemberResponseDTO
                {
                    MemberId = m.MembershipId,
                    GroupId  = m.GroupId,
                    UserId   = m.UserId,
                    UserName = m.User.FullName,
                    Email    = m.User.Email,
                    IsLeader = m.IsLeader.GetValueOrDefault(false),
                    JoinedAt = m.JoinedAt,
                    LeftAt   = m.LeftAt
                })
                .ToList();

            return new StudentGroupResponseDTO
            {
                GroupId      = group.GroupId,
                GroupCode    = group.GroupCode,
                GroupName    = group.GroupName,
                LecturerId   = group.LecturerId,
                LecturerName = group.Lecturer?.FullName ?? "Unknown",
                LeaderId     = group.LeaderId,
                LeaderName   = group.Leader?.FullName,
                Status       = group.Status,
                MemberCount  = activeMembers.Count,
                Members      = activeMembers,
                CreatedAt    = group.CreatedAt,
                UpdatedAt    = group.UpdatedAt
            };
        }

        private static RequirementResponseDTO MapToRequirementResponse(Requirement r)
        {
            return new RequirementResponseDTO
            {
                RequirementId = r.RequirementId,
                ProjectId = r.ProjectId,
                JiraIssueId = r.JiraIssueId,
                JiraIssueKey = r.JiraIssue?.IssueKey,
                RequirementCode = r.RequirementCode,
                Title = r.Title,
                Description = r.Description,
                RequirementType = r.RequirementType.ToString(),
                IssueType = r.JiraIssue?.IssueType,
                Priority = r.Priority.ToString(),
                JiraStatus = r.JiraIssue?.Status,
                CreatedBy = r.CreatedBy,
                CreatedByName = r.CreatedByNavigation?.FullName,
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt
            };
        }

        private static TaskResponseDTO MapToTaskResponse(DAL.Models.Task t)
        {
            return new TaskResponseDTO
            {
                TaskId = t.TaskId,
                RequirementId = t.RequirementId,
                RequirementCode = t.Requirement?.RequirementCode,
                JiraIssueId = t.JiraIssueId,
                JiraIssueKey = t.JiraIssue?.IssueKey,
                AssignedTo = t.AssignedTo,
                AssignedToName = t.AssignedToNavigation?.FullName,
                Title = t.Title,
                Description = t.Description,
                Status = t.Status.ToString(),
                Priority = t.Priority.ToString(),
                DueDate = t.DueDate,
                CompletedAt = t.CompletedAt,
                SprintId = t.JiraIssue?.SprintId,
                SprintName = t.JiraIssue?.SprintName,
                CreatedAt = t.CreatedAt,
                UpdatedAt = t.UpdatedAt
            };
        }
    }
}
