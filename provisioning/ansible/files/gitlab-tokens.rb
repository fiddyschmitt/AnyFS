# Run by gitlab-seed.sh through `gitlab-rails runner`: root's and the tester's personal access tokens, with the values
# settings.yml gives them, so the tests know them without asking GitLab. A token that works is kept; one that lapsed or
# was revoked is made again.
def ensure_token(user, value, name, scopes)
  existing = PersonalAccessToken.find_by_token(value)
  if existing && existing.user_id == user.id && existing.active?
    puts "#{name}: kept (expires #{existing.expires_at})"
    return
  end

  existing&.revoke!
  attributes = { name: name, scopes: scopes, expires_at: 360.days.from_now }
  if PersonalAccessToken.column_names.include?('organization_id') && defined?(Organizations::Organization)
    attributes[:organization] = Organizations::Organization.first
  end
  token = user.personal_access_tokens.build(attributes)
  token.set_token(value)
  token.save!
  # a token of this name with another value (settings.yml changed it) works no more
  user.personal_access_tokens.active.where(name: name).where.not(id: token.id).each(&:revoke!)
  puts "#{name}: made"
end

root = User.find_by_username('root') or abort('no root user')
ensure_token(root, ENV.fetch('ROOT_TOKEN'), 'anyfs-lab-seed', %w[api sudo admin_mode create_runner])

tester = User.find_by_username(ENV.fetch('TESTER_USER'))
if tester
  ensure_token(tester, ENV.fetch('TESTER_TOKEN'), 'anyfs-lab-tests', %w[read_api read_repository])
else
  puts "#{ENV.fetch('TESTER_USER')}: not a GitLab user yet (signs in through LDAP first)"
end
